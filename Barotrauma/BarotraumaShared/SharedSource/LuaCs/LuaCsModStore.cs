using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Barotrauma
{
    partial class LuaCsSetup
    {
        public class LuaCsModStore
        {
            public abstract class ModStore<T, TStore>
            {
                protected Dictionary<string, TStore> store;

                public TStore Set(string name, TStore value) => store[name] = value;
                public TStore Get(string name) => store[name];

                public ModStore(Dictionary<string, TStore> store) => this.store = store;

                public abstract bool Equals(T value);
            }
            public class LuaModStore : ModStore<string, DynValue>
            {
                public string Name;

                public LuaModStore(Dictionary<string, DynValue> store) : base(store) { }
                public override bool Equals(string value) => Name == value;
            }
            public class CsModStore : ModStore<ACsMod, object>
            {
                public ACsMod Mod;

                public CsModStore(Dictionary<string, object> store) : base(store) { }
                public override bool Equals(ACsMod value) => Mod == value;
            }

            private HashSet<LuaModStore> luaModInterface;
            private HashSet<CsModStore> csModInterface;

            public LuaCsModStore()
            {
                luaModInterface = new HashSet<LuaModStore>();
                csModInterface = new HashSet<CsModStore>();
            }

            public void Initialize()
            {
                UserData.RegisterType<LuaModStore>();
                UserData.RegisterType<CsModStore>();
                var msType = UserData.RegisterType<LuaCsModStore>();
                var msDesc = (StandardUserDataDescriptor)msType;

                typeof(StandardUserDataDescriptor).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).ToList().ForEach(m =>
                {
                    if (
                        m.Name.Contains("Register")
                    )
                    {
                        msDesc.AddMember(m.Name, new MethodMemberDescriptor(m, InteropAccessMode.Default));
                    }
                });
            }
            public void Clear()
            {
                luaModInterface.Clear();
                csModInterface.Clear();
            }

            protected LuaModStore Register(string modName)
            {
                if (luaModInterface.Any(i => i.Equals(modName)))
                {
                    LuaCsLogger.HandleException(new ArgumentException($"'{modName}' entry already registered"), LuaCsMessageOrigin.LuaMod);
                    return null;
                }

                var newHandle = new LuaModStore(new Dictionary<string, DynValue>());
                if (luaModInterface.Add(newHandle)) return newHandle;
                else return null;
            }
            [MoonSharpHidden]
            public CsModStore Register(ACsMod mod)
            {
                if (csModInterface.Any(i => i.Equals(mod)))
                {
                    LuaCsLogger.HandleException(new ArgumentException($"'{mod.GetType().FullName}' entry already registered"), LuaCsMessageOrigin.CSharpMod);
                    return null;
                }

                var newHandle = new CsModStore(new Dictionary<string, object>());
                if (csModInterface.Add(newHandle)) return newHandle;
                else return null;
            }

            public CsModStore GetCsStore(string modName) {
                var result = csModInterface.Where(i => i.Mod.GetType().FullName == modName).FirstOrDefault();
                if (result != null)
                {
                    if (!result.Mod.IsDisposed) return result;
                    else
                    {
                        csModInterface.Remove(result);
                        return null;
                    }
                }
                else return null;
            }
            protected LuaModStore GetLuaStore(string modName) => luaModInterface.Where(i => i.Name == modName).FirstOrDefault();
        }
    }
}
