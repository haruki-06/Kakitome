using Kakitome.Application.Models;
using Windows.Networking.Connectivity;

namespace Kakitome.Infrastructure.Network;

/// <summary>Metered = the Windows connection profile charges by data (fixed/variable cost, roaming or over the limit).</summary>
public sealed class WindowsNetworkCostProbe : INetworkCostProbe
{
    public bool IsMetered
    {
        get
        {
            try
            {
                var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
                return cost is not null
                    && (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming || cost.OverDataLimit);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                // Unknown: treat as metered so nothing large is downloaded by itself.
                return true;
            }
        }
    }
}
