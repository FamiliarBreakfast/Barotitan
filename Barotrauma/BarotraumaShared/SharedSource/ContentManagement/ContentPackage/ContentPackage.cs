﻿#nullable enable
using Barotrauma.Extensions;
using Barotrauma.Steam;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml;
using Barotrauma.IO;
using Steamworks.Data;

namespace Barotrauma
{
    public abstract class ContentPackage
    {
        public readonly record struct LoadError(string Message, Exception? Exception)
        {
            public override string ToString()
                => Message
                   + (Exception is { StackTrace: var stackTrace }
                       ? '\n' + stackTrace.CleanupStackTrace()
                       : string.Empty);
        }

        public static readonly Version MinimumHashCompatibleVersion = new Version(1, 1, 0, 0);
        
        public const string LocalModsDir = "LocalMods";
        public static readonly string WorkshopModsDir = Barotrauma.IO.Path.Combine(
            SaveUtil.DefaultSaveFolder,
            "WorkshopMods",
            "Installed");

        public const string FileListFileName = "filelist.xml";
        public const string DefaultModVersion = "1.0.0";

        public string Name { get; private set; }
        public readonly ImmutableArray<string> AltNames;
        public string Path { get; private set; }
        public string Dir => Barotrauma.IO.Path.GetDirectoryName(Path) ?? "";
        public readonly Option<ContentPackageId> UgcId;

        public readonly Version GameVersion;
        public readonly string ModVersion;

        public enum UgcStatus
        {
            NotFetched = 0,
            Fetching = 1,
            Fetched = 2,
            Unavailable = 3
        }

        public UgcStatus UgcItemStatus { get; private set; }

        /// <summary>
        /// Ugc item (workshop item) data that this content package corresponds to. Needs to be fetched with <see cref="TryFetchUgcItem(Action)"/>.
        /// You can also check <see cref="UgcItemStatus"/> to see if the item is available or not.
        /// </summary>
        public Option<Steamworks.Ugc.Item> UgcItem
        {
            get;
            private set;
        }

        public Md5Hash Hash { get; private set; }
        public readonly Option<SerializableDateTime> InstallTime;

        public ImmutableArray<ContentFile> Files { get; private set; }
        
        /// <summary>
        /// Errors that occurred when loading this content package.
        /// Currently, all errors are considered fatal and the game
        /// will refuse to load a content package that has any errors.
        /// </summary>
        public ImmutableArray<LoadError> FatalLoadErrors { get; private set; }

        /// <summary>
        /// An error that occurred when trying to enable this mod.
        /// This field doesn't directly affect whether or not this mod
        /// can be enabled, but if it's been set to anything other than
        /// Option.None then the game has already refused to enable it
        /// at least once.
        /// </summary>
        public Option<ContentPackageManager.LoadProgress.Error> EnableError { get; private set; }
            = Option.None;

        private readonly HashSet<PublishedFileId> missingDependencies = new HashSet<PublishedFileId>();
        /// <summary>
        /// An error caused by missing dependencies (Workshop items required by the package).
        /// Can be safe to ignore.
        /// </summary>
        public IEnumerable<PublishedFileId> MissingDependencies => missingDependencies;

        public bool HasAnyErrors => FatalLoadErrors.Length > 0 || EnableError.IsSome() || missingDependencies.Any();

        public async Task<bool> IsUpToDate()
        {
            if (!UgcId.TryUnwrap(out var ugcId)) { return true; }
            if (ugcId is not SteamWorkshopId steamWorkshopId) { return true; }
            if (!InstallTime.TryUnwrap(out var installTime)) { return true; }
            
            Option<Steamworks.Ugc.Item> itemOption = await SteamManager.Workshop.GetItem(steamWorkshopId.Value);
            if (!itemOption.TryUnwrap(out var item)) { return true; }
            return item.LatestUpdateTime <= installTime.ToUtcValue();
        }

        public int Index => ContentPackageManager.EnabledPackages.IndexOf(this);

        /// <summary>
        /// Does the content package include some content that needs to match between all players in multiplayer. 
        /// </summary>
        public bool HasMultiplayerSyncedContent;

        protected ContentPackage(XDocument doc, string path)
        {
            using var errorCatcher = DebugConsole.ErrorCatcher.Create();
            
            Path = path.CleanUpPathCrossPlatform();
            XElement rootElement = doc.Root ?? throw new NullReferenceException("XML document is invalid: root element is null.");

            Name = rootElement.GetAttributeString("name", "").Trim();
            AltNames = rootElement.GetAttributeStringArray("altnames", Array.Empty<string>())
                .Select(n => n.Trim()).ToImmutableArray();
            UInt64 steamWorkshopId = rootElement.GetAttributeUInt64("steamworkshopid", 0);

            if (Name.IsNullOrWhiteSpace() && AltNames.Any())
            {
                Name = AltNames.First();
            }

            UgcId = steamWorkshopId != 0
                ? Option<ContentPackageId>.Some(new SteamWorkshopId(steamWorkshopId))
                : Option<ContentPackageId>.None();

            GameVersion = rootElement.GetAttributeVersion("gameversion", GameMain.Version);
            ModVersion = rootElement.GetAttributeString("modversion", DefaultModVersion);
            InstallTime = rootElement.GetAttributeDateTime("installtime");

            var fileResults = rootElement.Elements()
                .Where(e => !ContentFile.IsLegacyContentType(e, this, logWarning: true))
                .Select(e => ContentFile.CreateFromXElement(this, e))
                .ToArray();

            Files = fileResults
                .Successes()
                .ToImmutableArray();

            FatalLoadErrors = fileResults
                .Failures()
                .ToImmutableArray();

            AssertCondition(!string.IsNullOrEmpty(Name), $"{nameof(Name)} is null or empty");

            HasMultiplayerSyncedContent = Files.Any(f => !f.NotSyncedInMultiplayer);

            Hash = CalculateHash();
            var expectedHash = rootElement.GetAttributeString("expectedhash", "");
            if (HashMismatches(expectedHash))
            {
                FatalLoadErrors = FatalLoadErrors.Add(
                    new LoadError(
                        Message: $"Hash calculation returned {Hash.StringRepresentation}, expected {expectedHash}",
                        Exception: null
                    ));
            }

            FatalLoadErrors = FatalLoadErrors
                .Concat(errorCatcher.Errors.Select(err => new LoadError(err.Text, null)))
                .ToImmutableArray();
        }

        public bool HashMismatches(string expectedHash)
            => GameVersion >= MinimumHashCompatibleVersion &&
               !expectedHash.IsNullOrWhiteSpace() &&
               !expectedHash.Equals(Hash.StringRepresentation, StringComparison.OrdinalIgnoreCase);

        public IEnumerable<T> GetFiles<T>() where T : ContentFile => Files.OfType<T>();

        public IEnumerable<ContentFile> GetFiles(Type type)
            => !type.IsSubclassOf(typeof(ContentFile))
                ? throw new ArgumentException($"Type must be subclass of ContentFile, got {type.Name}")
                : Files.Where(f => f.GetType() == type || f.GetType().IsSubclassOf(type));

        public bool NameMatches(Identifier name)
            => Name == name || AltNames.Any(n => n == name);

        public bool NameMatches(string name)
            => NameMatches(name.ToIdentifier());
        
        public static Result<ContentPackage, Exception> TryLoad(string path)
        {
            var (success, failure) = Result<ContentPackage, Exception>.GetFactoryMethods();
            
            XDocument doc = XMLExtensions.TryLoadXml(path);

            try
            {
                return success(doc.Root.GetAttributeBool("corepackage", false)
                    ? new CorePackage(doc, path)
                    : new RegularPackage(doc, path));
            }
            catch (Exception e)
            {
                return failure(e.GetInnermost());
            }
        }

        public Md5Hash CalculateHash(bool logging = false, string? name = null, string? modVersion = null)
        {
            using IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            
            if (logging)
            {
                DebugConsole.NewMessage("****************************** Calculating content package hash " + Name);
            }

            foreach (ContentFile file in Files)
            {
                try
                {
                    var hash = file.Hash;
                    if (logging)
                    {
                        DebugConsole.NewMessage("   " + file.Path + ": " + hash.StringRepresentation);
                    }                    
                    incrementalHash.AppendData(hash.ByteRepresentation);
                }

                catch (Exception e)
                {
                    DebugConsole.ThrowError($"Error while calculating the MD5 hash of the content package \"{Name}\" (file path: {Path}). The content package may be corrupted. You may want to delete or reinstall the package.", e);
                    break;
                }             
            }

            string selectedName = name ?? Name;
            if (!selectedName.IsNullOrEmpty())
            {
                incrementalHash.AppendData(Encoding.UTF8.GetBytes(selectedName));
            }
            incrementalHash.AppendData(Encoding.UTF8.GetBytes(modVersion ?? ModVersion));

            var md5Hash = Md5Hash.BytesAsHash(incrementalHash.GetHashAndReset());
            if (logging)
            {
                DebugConsole.NewMessage("****************************** Package hash: " + md5Hash.StringRepresentation);
            }

            return md5Hash;
        }

        protected void AssertCondition(bool condition, string errorMsg)
        {
            if (!condition)
            {
                FatalLoadErrors = FatalLoadErrors.Add(new LoadError(errorMsg, null));
            }
        }

        public void AddMissingDependency(PublishedFileId missingItemID)
        {
            missingDependencies.Add(missingItemID);
        }

        public void ClearMissingDependencies()
        {
            missingDependencies.Clear();
        }

        public void LoadFilesOfType<T>() where T : ContentFile
        {
            Files.Where(f => f is T).ForEach(f => f.LoadFile());
        }

        public void UnloadFilesOfType<T>() where T : ContentFile
        {
            Files.Where(f => f is T).ForEach(f => f.UnloadFile());
        }

        public enum LoadResult
        {
            Success,
            Failure
        }

        public LoadResult LoadContent()
        {
            foreach (var p in LoadContentEnumerable())
            {
                if (p.Result.IsFailure) { return LoadResult.Failure; }
            }
            return LoadResult.Success;
        }
        
        public IEnumerable<ContentPackageManager.LoadProgress> LoadContentEnumerable()
        {
            using var errorCatcher = DebugConsole.ErrorCatcher.Create();

            ContentFile[] getFilesToLoad(Predicate<ContentFile> predicate)
                => Files.Where(predicate.Invoke).ToArray()
#if DEBUG
                        //The game should be able to work just fine with a completely arbitrary file load order.
                        //To make sure we don't mess this up, debug builds randomize it so it has a higher chance
                        //of breaking anything that's not implemented correctly.
                        .Randomize(Rand.RandSync.Unsynced)
#endif
                    ;

            IEnumerable<ContentPackageManager.LoadProgress> loadFiles(ContentFile[] filesToLoad, int indexOffset)
            {
                for (int i = 0; i < filesToLoad.Length; i++)
                {
                    Exception? exception = null;

                    try
                    {
                        //do not allow exceptions thrown here to crash the game
                        filesToLoad[i].LoadFile();
                    }
                    catch (Exception e)
                    {
                        var innermost = e.GetInnermost();
                        DebugConsole.LogError($"Failed to load \"{filesToLoad[i].Path}\": {innermost.Message}\n{innermost.StackTrace}", contentPackage: this);
                        exception = e;
                    }
                    if (exception != null)
                    {
                        yield return ContentPackageManager.LoadProgress.Failure(exception);
                        yield break;
                    }

                    if (errorCatcher.Errors.Any())
                    {
                        yield return ContentPackageManager.LoadProgress.Failure(errorCatcher.Errors.Select(e => e.Text));
                        yield break;
                    }
                    yield return ContentPackageManager.LoadProgress.Progress((i + indexOffset) / (float)Files.Length);
                }
            }

            //Load the UI and text files first. This is to allow the game
            //to render the text in the loading screen as soon as possible.
            var priorityFiles = getFilesToLoad(f => f is UIStyleFile or TextFile);

            var remainder = getFilesToLoad(f => !priorityFiles.Contains(f));

            var loadEnumerable =
                loadFiles(priorityFiles, 0)
                    .Concat(loadFiles(remainder, priorityFiles.Length));
            
            foreach (var p in loadEnumerable)
            {
                if (p.Result.TryUnwrapFailure(out var failure))
                {
                    errorCatcher.Dispose();
                    UnloadContent();
                    EnableError = Option.Some(failure);
                    yield return p;
                    yield break;
                }
                yield return p;
            }
            errorCatcher.Dispose();
        }

        public void TryFetchUgcDescription(Action<string?> onFinished)
        {
            TryFetchUgcItem((Steamworks.Ugc.Item? item) =>
            {
                onFinished?.Invoke(item?.Description ?? string.Empty);
            });
        }

        public void TryFetchUgcChildren(Action<PublishedFileId[]?> onFinished)
        {
            TryFetchUgcItem((Steamworks.Ugc.Item? item) =>
            {
                onFinished?.Invoke(item?.Children ?? Array.Empty<PublishedFileId>());
            });
        }

        private void TryFetchUgcItem(Action<Steamworks.Ugc.Item?> onFinished)
        {
            switch (UgcItemStatus)
            {
                case UgcStatus.NotFetched:
                    TryFetchUgcItem(onFinished: () =>
                    {
                        if (UgcItemStatus == UgcStatus.Fetched &&
                            UgcItem.TryUnwrap(out var cachedItem))
                        {
                            onFinished?.Invoke(cachedItem);
                        }
                    });
                    break;
                case UgcStatus.Fetched when UgcItem.TryUnwrap(out var cachedItem):
                    onFinished?.Invoke(cachedItem);
                    break;
                default:
                    onFinished?.Invoke(null);
                    break;
            }
        }

        /// <summary>
        /// Attempts to fetch the UgcItem (workshop item) data from Steamworks, and if successful, caches it in <see cref="UgcItem"/>.
        /// </summary>
        /// <param name="onFinished">Triggers when the query finishes or fails (or immediately if the item has been already cached)</param>
        public void TryFetchUgcItem(Action onFinished)
        {
            if (UgcItemStatus != UgcStatus.NotFetched)
            {
                onFinished?.Invoke();
            }
            if (!UgcId.TryUnwrap(out var ugcId) || ugcId is not SteamWorkshopId workshopId) 
            {
                UgcItemStatus = UgcStatus.Unavailable;
                return;
            }
            
            UgcItemStatus = UgcStatus.Fetching;
            TaskPool.Add($"PrepareToShow{UgcId}Info", SteamManager.Workshop.GetItem(workshopId.Value),
                task =>
                {
                    if (!task.TryGetResult(out Option<Steamworks.Ugc.Item> itemOption) || !itemOption.TryUnwrap(out var item))
                    {
                        UgcItemStatus = UgcStatus.Unavailable;
                        return;
                    }
                    UgcItem = Option<Steamworks.Ugc.Item>.Some(item);
                    UgcItemStatus = UgcStatus.Fetched;
                    onFinished?.Invoke();
                });
        }

        public void UnloadContent()
        {
            Files.ForEach(f => f.UnloadFile());
        }

        public void ReloadSubsAndItemAssemblies()
        {
            XDocument doc = XMLExtensions.TryLoadXml(Path);
            List<ContentFile> newFileList = new List<ContentFile>();
            XElement rootElement = doc.Root ?? throw new NullReferenceException("XML document is invalid: root element is null.");
            
            var fileResults = rootElement.Elements()
                .Where(e => !ContentFile.IsLegacyContentType(e, this, logWarning: true))
                .Select(e => ContentFile.CreateFromXElement(this, e))
                .ToArray();

            foreach (var file in fileResults.Successes())
            {
                if (file is BaseSubFile or ItemAssemblyFile)
                {
                    newFileList.Add(file);
                }
                else
                {
                    var existingFile = Files.FirstOrDefault(f => f.Path == file.Path);
                    newFileList.Add(existingFile ?? file);
                }
            }

            UnloadFilesOfType<BaseSubFile>();
            UnloadFilesOfType<ItemAssemblyFile>();
            Files = newFileList.ToImmutableArray();
            Hash = CalculateHash();
            LoadFilesOfType<BaseSubFile>();
            LoadFilesOfType<ItemAssemblyFile>();
        }

        public static bool PathAllowedAsLocalModFile(string path)
        {
#if DEBUG
            if (GameMain.VanillaContent.Files.Any(f => f.Path == path))
            {
                //file is in vanilla package, this is allowed
                return true;
            }
#endif

            while (true)
            {
                string temp = Barotrauma.IO.Path.GetDirectoryName(path) ?? "";
                if (string.IsNullOrEmpty(temp)) { break; }
                path = temp;
            }
            return path == LocalModsDir;
        }

        public void LogErrors()
        {
            if (!FatalLoadErrors.Any())
            {
                return;
            }

            DebugConsole.AddWarning(
                $"The following errors occurred while loading the content package \"{Name}\". The package might not work correctly.\n" +
                string.Join('\n', FatalLoadErrors.Select(errorToStr)),
                this);

            static string errorToStr(LoadError error)
                => error.ToString();
        }

        public bool TryRenameLocal(string newName)
        {
            if (!ContentPackageManager.LocalPackages.Contains(this)) { return false; }

            if (newName.IsNullOrWhiteSpace())
            {
                DebugConsole.ThrowError($"New name is blank!");
                return false;
            }

            string newDir = IO.Path.Combine(IO.Path.GetFullPath(LocalModsDir), File.SanitizeName(newName));
            if (ContentPackageManager.LocalPackages.Any(lp => lp.NameMatches(newName)) || Directory.Exists(newDir))
            {
                DebugConsole.ThrowError($"A local package with the name or directory \"{newName}\" already exists!");
                return false;
            }

            XDocument doc = XMLExtensions.TryLoadXml(Path);
            doc.Root!.SetAttributeValue("name", newName);
            using (IO.XmlWriter writer = IO.XmlWriter.Create(Path, new XmlWriterSettings { Indent = true }))
            {
                doc.WriteTo(writer);
                writer.Flush();
            }

            Directory.Move(Dir, newDir);
            return true;
        }

        public bool TryDeleteLocal() => ContentPackageManager.LocalPackages.Contains(this) && Directory.TryDelete(Dir);

        public bool TryCreateLocalFromWorkshop()
        {
            if (!ContentPackageManager.WorkshopPackages.Contains(this)) { return false; }

            string newDir = IO.Path.Combine(IO.Path.GetFullPath(LocalModsDir), File.SanitizeName(Name));
            if (ContentPackageManager.LocalPackages.Any(lp => lp.NameMatches(Name)) || Directory.Exists(newDir))
            {
                DebugConsole.ThrowError($"A local package with the name or directory \"{Name}\" already exists!");
                return false;
            }

            Directory.Copy(Dir, newDir);
            return true;
        }
    }
}
