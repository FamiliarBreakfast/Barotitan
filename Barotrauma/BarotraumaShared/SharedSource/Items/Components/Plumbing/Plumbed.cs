using System.Collections.Generic;

namespace Barotrauma.Items.Components
{
    class Plumbed : Powered
    {
        private static readonly List<Powered> plumbedList = new List<Powered>();
        public static IEnumerable<Powered> PlumbedList => plumbedList;
        public Dictionary<int, int> fluids = new Dictionary<int, int>();
        public Plumbed(Item item, ContentXElement element) : base(item, element)
        {
            plumbedList.Add(this);
            //InitProjectSpecific(element); todo: client-less?
        }
    }
}
