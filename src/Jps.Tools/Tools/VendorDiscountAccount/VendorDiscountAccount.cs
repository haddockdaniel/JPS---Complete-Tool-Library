using System.Windows;
using Jps.Core.Modules;

namespace Jps.Tools.Tools.VendorDiscountAccount
{
    /// <summary>Vendor Discount Account Updater: swaps Vendor.VenDiscAcct. The page is shared with Vendor GL Default Dist Account Updater.</summary>
    public sealed class VendorDiscountAccountTool : IJurisTool
    {
        public string Id => "JPS.VENDISCACCT";
        public FrameworkElement CreateView(IToolContext context) => new VendorAccountSwapView(context, VendorAccountField.Discount);
    }
}
