using System.Windows;
using Jps.Core.Modules;
using Jps.Tools.Tools.VendorDiscountAccount;

namespace Jps.Tools.Tools.VendorDistAccount
{
    /// <summary>
    /// Vendor GL Default Dist Account Updater: swaps Vendor.VenDefaultDistAcct. Uses the shared page and service in
    /// Tools\VendorDiscountAccount (VendorAccountSwapView / VendorAccountSwapService); see that view for the fixes made.
    /// </summary>
    public sealed class VendorDistAccountTool : IJurisTool
    {
        public string Id => "JPS.VENGLDIST";
        public FrameworkElement CreateView(IToolContext context) => new VendorAccountSwapView(context, VendorAccountField.DefaultDist);
    }
}
