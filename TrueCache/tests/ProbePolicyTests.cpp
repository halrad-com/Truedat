#include "../probe/ProbePolicy.h"
#include <array>
#include <iostream>
#include <span>
#include <string_view>

namespace {
int failures = 0;
void Check(bool condition, std::string_view name) {
    if (!condition) {
        std::cerr << "FAIL: " << name << '\n';
        ++failures;
    }
}
template<std::size_t N>
tc::ProbeOptions Parse(const std::array<std::wstring_view, N>& args) {
    return tc::ParseProbeArguments(std::span<const std::wstring_view>(args));
}
}

int main() {
    using namespace std::literals;
    Check(Parse(std::array{L"inspect"sv}).command == tc::ProbeCommand::Inspect,
        "inspect selects the read-only command");
    const auto verbose = Parse(std::array{L"inspect"sv, L"--verbose"sv});
    Check(verbose.command == tc::ProbeCommand::Inspect && verbose.verbose,
        "verbose inspection is accepted");
    Check(Parse(std::array{L"--help"sv}).command == tc::ProbeCommand::Help,
        "help does not inspect");
    Check(tc::ParseProbeArguments({}).command == tc::ProbeCommand::Help,
        "no arguments show help");
    for (const auto verb : {L"create"sv, L"remove"sv, L"cycle"sv, L"run"sv}) {
        Check(Parse(std::array{verb}).command == tc::ProbeCommand::Invalid,
            "a mutating verb is rejected");
    }
    Check(Parse(std::array{L"inspect"sv, L"--force"sv}).command ==
        tc::ProbeCommand::Invalid, "an unknown option cannot bypass inspection");
    Check(Parse(std::array{L"--help"sv, L"create"sv}).command ==
        tc::ProbeCommand::Invalid, "help does not swallow trailing commands");
    Check(Parse(std::array{L"inspect"sv, L"--verbose"sv, L"--verbose"sv}).command ==
        tc::ProbeCommand::Invalid, "extra arguments are rejected");
    Check(Parse(std::array{L"Inspect"sv}).command == tc::ProbeCommand::Invalid,
        "command spelling is exact");

    // Same displayed version must not qualify a different binary.
    Check(tc::MatchesInspectedDriver(
        L"E706C3C549860D3EB848D30FBD2EA9DBF5E2A96A11F050FD93F73FCA53EECFB3"),
        "the recorded complete fingerprint matches");
    Check(tc::MatchesInspectedDriver(
        L"e706c3c549860d3eb848d30fbd2ea9dbf5e2a96a11f050fd93f73fca53eecfb3"),
        "hexadecimal letter case does not change identity");
    Check(!tc::MatchesInspectedDriver(
        L"E706C3C549860D3EB848D30FBD2EA9DBF5E2A96A11F050FD93F73FCA53EECFB0"),
        "a changed byte is unrecognized");
    Check(!tc::MatchesInspectedDriver(L"E706C3C5"),
        "a hash prefix does not establish identity");
    Check(!tc::MatchesInspectedDriver(L""),
        "missing identity is unrecognized");
    Check(!tc::MatchesInspectedDriver(
        L"E706C3C549860D3EB848D30FBD2EA9DBF5E2A96A11F050FD93F73FCA53EECFB3 "),
        "trailing data does not establish identity");
    std::cout << (failures == 0 ? "Probe policy checks passed.\n" :
        "Probe policy checks failed.\n");
    return failures == 0 ? 0 : 1;
}
