#pragma once
#include "NativeEvidence.h"
#include <span>
#include <string_view>

namespace tc {
enum class ProbeCommand { Help, Inspect, Invalid };
struct ProbeOptions {
    ProbeCommand command = ProbeCommand::Invalid;
    bool verbose = false;
};

inline ProbeOptions ParseProbeArguments(
    std::span<const std::wstring_view> arguments) noexcept {
    if (arguments.empty() ||
        (arguments.size() == 1 && arguments[0] == L"--help")) {
        return {ProbeCommand::Help, false};
    }
    if (arguments[0] == L"inspect") {
        if (arguments.size() == 1) {
            return {ProbeCommand::Inspect, false};
        }
        if (arguments.size() == 2 && arguments[1] == L"--verbose") {
            return {ProbeCommand::Inspect, true};
        }
    }
    return {};
}

inline bool MatchesInspectedDriver(std::wstring_view hash) noexcept {
    if (hash.size() != InspectedDriverSha256.size()) {
        return false;
    }
    for (std::size_t i = 0; i != hash.size(); ++i) {
        wchar_t ch = hash[i];
        if (ch >= L'a' && ch <= L'f') {
            ch = static_cast<wchar_t>(ch - L'a' + L'A');
        }
        if (ch != InspectedDriverSha256[i]) {
            return false;
        }
    }
    return true;
}
}
