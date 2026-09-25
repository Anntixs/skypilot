// SkyPilot X-Plane plugin - minimal non-blocking UDP socket for localhost traffic.
//
// Kept in its own translation unit so that the platform socket headers
// (Winsock on Windows, BSD sockets elsewhere) never mix with the X-Plane SDK headers.

#pragma once

#include <cstdint>
#include <string>

namespace skypilot {

/// An IPv4 address/port pair in host byte order.
struct UdpEndpoint {
    std::uint32_t address = 0;  ///< IPv4 address, host byte order (0x7F000001 = 127.0.0.1)
    std::uint16_t port = 0;     ///< UDP port, host byte order

    bool IsValid() const { return port != 0; }
    bool operator==(const UdpEndpoint& o) const { return address == o.address && port == o.port; }
    bool operator!=(const UdpEndpoint& o) const { return !(*this == o); }
};

/// Non-blocking IPv4 UDP socket.
class UdpSocket {
public:
    UdpSocket() = default;
    ~UdpSocket();
    UdpSocket(const UdpSocket&) = delete;
    UdpSocket& operator=(const UdpSocket&) = delete;

    /// Binds to 127.0.0.1:<port> and switches the socket to non-blocking mode.
    /// On failure returns false and fills `error` with a readable message.
    bool Open(std::uint16_t port, std::string& error);

    /// Closes the socket (safe to call repeatedly).
    void Close();

    bool IsOpen() const;

    /// Receives one datagram if available.
    /// Returns the number of bytes received (>0), 0 if nothing is pending, -1 on a hard error.
    /// The buffer is always zero-terminated.
    int Receive(char* buffer, int bufferSize, UdpEndpoint& from);

    /// Sends one datagram. Returns true on success.
    bool Send(const UdpEndpoint& to, const std::string& data);

private:
#if defined(_WIN32)
    std::uintptr_t sock_ = ~std::uintptr_t(0);  // SOCKET / INVALID_SOCKET
    bool wsaStarted_ = false;
#else
    int sock_ = -1;
#endif
};

} // namespace skypilot
