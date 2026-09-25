// SkyPilot X-Plane plugin - minimal non-blocking UDP socket (implementation).

#include "UdpSocket.h"

#if defined(_WIN32)
  #ifndef WIN32_LEAN_AND_MEAN
    #define WIN32_LEAN_AND_MEAN
  #endif
  #ifndef NOMINMAX
    #define NOMINMAX
  #endif
  #include <winsock2.h>
  #include <ws2tcpip.h>
  #include <mstcpip.h>
  // Not every SDK flavour defines this ioctl code.
  #ifndef SIO_UDP_CONNRESET
    #define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
  #endif
#else
  #include <arpa/inet.h>
  #include <cerrno>
  #include <cstring>
  #include <fcntl.h>
  #include <netinet/in.h>
  #include <sys/socket.h>
  #include <unistd.h>
#endif

namespace skypilot {

#if defined(_WIN32)
static SOCKET AsSocket(std::uintptr_t s) { return static_cast<SOCKET>(s); }
static const std::uintptr_t kInvalid = static_cast<std::uintptr_t>(INVALID_SOCKET);
#endif

UdpSocket::~UdpSocket() { Close(); }

bool UdpSocket::IsOpen() const
{
#if defined(_WIN32)
    return sock_ != kInvalid;
#else
    return sock_ >= 0;
#endif
}

bool UdpSocket::Open(std::uint16_t port, std::string& error)
{
    Close();

#if defined(_WIN32)
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
        error = "WSAStartup failed";
        return false;
    }
    wsaStarted_ = true;

    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) {
        error = "socket() failed, error " + std::to_string(WSAGetLastError());
        Close();
        return false;
    }
    sock_ = static_cast<std::uintptr_t>(s);

    // Without this, an ICMP "port unreachable" (the app was closed) makes the
    // next recvfrom() fail with WSAECONNRESET on Windows.
    BOOL reportReset = FALSE;
    DWORD bytesReturned = 0;
    WSAIoctl(s, SIO_UDP_CONNRESET, &reportReset, sizeof(reportReset), nullptr, 0, &bytesReturned, nullptr, nullptr);

    u_long nonBlocking = 1;
    if (ioctlsocket(s, FIONBIO, &nonBlocking) != 0) {
        error = "ioctlsocket(FIONBIO) failed, error " + std::to_string(WSAGetLastError());
        Close();
        return false;
    }
#else
    int s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s < 0) {
        error = std::string("socket() failed: ") + std::strerror(errno);
        return false;
    }
    sock_ = s;

    int flags = fcntl(s, F_GETFL, 0);
    if (flags < 0 || fcntl(s, F_SETFL, flags | O_NONBLOCK) < 0) {
        error = std::string("fcntl(O_NONBLOCK) failed: ") + std::strerror(errno);
        Close();
        return false;
    }
#endif

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

#if defined(_WIN32)
    if (bind(s, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0) {
        error = "bind(127.0.0.1:" + std::to_string(port) + ") failed, error " + std::to_string(WSAGetLastError());
        Close();
        return false;
    }
#else
    if (bind(s, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0) {
        error = "bind(127.0.0.1:" + std::to_string(port) + ") failed: " + std::strerror(errno);
        Close();
        return false;
    }
#endif
    return true;
}

void UdpSocket::Close()
{
#if defined(_WIN32)
    if (sock_ != kInvalid) {
        closesocket(AsSocket(sock_));
        sock_ = kInvalid;
    }
    if (wsaStarted_) {
        WSACleanup();
        wsaStarted_ = false;
    }
#else
    if (sock_ >= 0) {
        close(sock_);
        sock_ = -1;
    }
#endif
}

int UdpSocket::Receive(char* buffer, int bufferSize, UdpEndpoint& from)
{
    if (!IsOpen() || bufferSize < 2)
        return -1;

    sockaddr_in src{};
#if defined(_WIN32)
    int srcLen = sizeof(src);
    int n = recvfrom(AsSocket(sock_), buffer, bufferSize - 1, 0, reinterpret_cast<sockaddr*>(&src), &srcLen);
    if (n < 0) {
        int err = WSAGetLastError();
        // Nothing pending, or a stale ICMP error / oversized datagram: not fatal.
        if (err == WSAEWOULDBLOCK || err == WSAECONNRESET || err == WSAEMSGSIZE)
            return 0;
        return -1;
    }
#else
    socklen_t srcLen = sizeof(src);
    ssize_t n = recvfrom(sock_, buffer, static_cast<size_t>(bufferSize - 1), 0, reinterpret_cast<sockaddr*>(&src), &srcLen);
    if (n < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR || errno == ECONNREFUSED)
            return 0;
        return -1;
    }
#endif
    buffer[n] = '\0';
    from.address = ntohl(src.sin_addr.s_addr);
    from.port = ntohs(src.sin_port);
    // A zero-length datagram carries nothing useful; report it as "nothing pending"
    // would stop the drain loop early, so report it as a 1-byte empty string instead.
    return n > 0 ? static_cast<int>(n) : 1;
}

bool UdpSocket::Send(const UdpEndpoint& to, const std::string& data)
{
    if (!IsOpen() || !to.IsValid())
        return false;

    sockaddr_in dst{};
    dst.sin_family = AF_INET;
    dst.sin_port = htons(to.port);
    dst.sin_addr.s_addr = htonl(to.address);

#if defined(_WIN32)
    int n = sendto(AsSocket(sock_), data.data(), static_cast<int>(data.size()), 0,
                   reinterpret_cast<const sockaddr*>(&dst), sizeof(dst));
#else
    ssize_t n = sendto(sock_, data.data(), data.size(), 0, reinterpret_cast<const sockaddr*>(&dst), sizeof(dst));
#endif
    return n == static_cast<decltype(n)>(data.size());
}

} // namespace skypilot
