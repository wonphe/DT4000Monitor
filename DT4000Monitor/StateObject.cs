using System.Net.Sockets;
using System.Text;

namespace DT4000Monitor
{
    public class StateObject
    {
        // Client  socket.
        public Socket WorkSocket;
        // Size of receive buffer.
        public const int BufferSize = 1024;
        // Receive buffer.
        public byte[] Buffer = new byte[BufferSize];
        // Received data string.
        public StringBuilder Sb = new StringBuilder();
    }
}
