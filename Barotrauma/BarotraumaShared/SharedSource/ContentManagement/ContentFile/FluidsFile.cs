using Barotrauma.Items.Components;

namespace Barotrauma
{
    sealed class FluidsFile : GenericPrefabFile<FluidPrefab>
    {
        public FluidsFile(ContentPackage contentPackage, ContentPath path) : base(contentPackage, path) { }
        protected override bool MatchesSingular(Identifier identifier) => identifier == "fluid";

        protected override bool MatchesPlural(Identifier identifier) => identifier == "fluids";

        protected override PrefabCollection<FluidPrefab> Prefabs => FluidPrefab.Prefabs;
        protected override FluidPrefab CreatePrefab(ContentXElement element)
        {
            return new FluidPrefab(this, element);
        }
    }
}
