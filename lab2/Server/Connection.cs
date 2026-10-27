using System.Net.Sockets;
using System.Text;

namespace Labs.Lab2.Server;

/// The socket of one client. The client thread and the workers both send on it,
/// so sending takes a lock: the bytes of two lines never mix.
public sealed class Connection
{
    private readonly Socket _socket;
    private readonly object _sendLock = new();

    public Connection(Socket socket)
    {
        _socket = socket;
        Endpoint = socket.RemoteEndPoint?.ToString() ?? "?";

        // A lost network sends nothing, so Receive would wait forever. Keepalive probes after
        // 10 s of silence, every 2 s; after 3 unanswered probes Receive fails with TimedOut.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);

        // A client that never reads makes Send block; a worker must not hang there.
        socket.SendTimeout = 5000;
    }

    /// Address and port of the client, for the log.
    public string Endpoint { get; }

    /// 0 means the client closed the connection.
    public int Receive(byte[] buffer) => _socket.Receive(buffer);

    /// Sends one line. On failure closes the socket, which also ends the client thread's Receive.
    public bool Send(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_sendLock)
        {
            try
            {
                // Send may take only part of the bytes: repeat until all are sent
                int sent = 0;
                while (sent < bytes.Length)
                    sent += _socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                return true;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                _socket.Close();
                return false;
            }
        }
    }

    public void Close()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // already broken or closed: nothing left to say goodbye to
        }
        _socket.Close();
    }
}
