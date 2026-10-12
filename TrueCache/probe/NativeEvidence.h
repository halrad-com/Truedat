#pragma once
#include <guiddef.h>
#include <string_view>

namespace tc {
// These identify the binary inspected during development, not a supported ABI.
inline constexpr std::wstring_view InspectedDriverVersion = L"10.0.26100.8972";
inline constexpr std::wstring_view InspectedDriverSha256 =
    L"E706C3C549860D3EB848D30FBD2EA9DBF5E2A96A11F050FD93F73FCA53EECFB3";

// RamdiskBusInterface in that binary's public symbols, .rdata RVA 0x4270.
// Used only for read-only interface discovery. No private IOCTL is issued.
inline constexpr GUID RamdiskBusInterface{
    0x5dc52df0, 0x2f8a, 0x410f,
    {0x80, 0xe4, 0x05, 0xf8, 0x10, 0xe7, 0xab, 0x8a}
};
}
