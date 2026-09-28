using System.Net;
using System.Net.Sockets;

namespace XrayUI.Helpers
{
    public static class LoopbackPort
    {
        /// <summary>
        /// Asks the OS for a free loopback TCP port by binding to port 0 and reading the assigned
        /// port back. There is a tiny race between releasing it here and xray binding it, but it is
        /// the standard ephemeral-port allocation trick.
        /// </summary>
        public static int PickFree()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
