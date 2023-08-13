

namespace Barotrauma.Items.Components
{
    class PlumbingTransfer : PowerTransfer
    {
        public PlumbingTransfer(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
            //canTransfer = true;
        }
    }
}
