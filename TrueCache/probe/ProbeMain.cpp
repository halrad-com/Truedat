#include "ProbePolicy.h"
#include "../Diagnostics.h"
#include "../WindowsResources.h"
#include <softpub.h>
#include <wintrust.h>
#include <array>
#include <cstddef>
#include <iostream>
#include <cstdio>
#include <fcntl.h>
#include <io.h>
#include <optional>
#include <span>
#include <string>
#include <vector>

#if !defined(_M_X64)
#error TrueCache probe currently targets Windows x64.
#endif

namespace {
using tc::Log;
using tc::LogLevel;

bool CngSucceeded(NTSTATUS status, std::wstring_view api) {
    if (status >= 0) {
        return true;
    }
    Log(LogLevel::Error, api, L"NTSTATUS=" + tc::HexCode(static_cast<DWORD>(status)));
    return false;
}

std::optional<std::wstring> SystemDirectory() {
    std::vector<wchar_t> buffer(MAX_PATH);
    for (;;) {
        const UINT length = GetSystemDirectoryW(buffer.data(),
            static_cast<UINT>(buffer.size()));
        if (length == 0) {
            tc::LogWindowsError(L"GetSystemDirectoryW", GetLastError());
            return std::nullopt;
        }
        if (length < buffer.size()) {
            return std::wstring(buffer.data(), length);
        }
        buffer.resize(static_cast<std::size_t>(length) + 1);
    }
}

bool Seek(HANDLE file, LONGLONG offset) {
    LARGE_INTEGER position{};
    position.QuadPart = offset;
    if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN)) {
        tc::LogWindowsError(L"SetFilePointerEx", GetLastError());
        return false;
    }
    return true;
}

std::optional<std::wstring> HashFile(HANDLE file) {
    if (!Seek(file, 0)) {
        return std::nullopt;
    }
    BCRYPT_ALG_HANDLE algorithmRaw = nullptr;
    if (!CngSucceeded(BCryptOpenAlgorithmProvider(&algorithmRaw,
            BCRYPT_SHA256_ALGORITHM, nullptr, 0), L"BCryptOpenAlgorithmProvider")) {
        return std::nullopt;
    }
    tc::UniqueAlgorithm algorithm(algorithmRaw);
    BCRYPT_HASH_HANDLE hashRaw = nullptr;
    if (!CngSucceeded(BCryptCreateHash(algorithm.get(), &hashRaw,
            nullptr, 0, nullptr, 0, 0), L"BCryptCreateHash")) {
        return std::nullopt;
    }
    tc::UniqueHash hash(hashRaw);
    std::array<UCHAR, 65536> buffer{};
    for (;;) {
        DWORD read = 0;
        if (!ReadFile(file, buffer.data(), static_cast<DWORD>(buffer.size()),
                &read, nullptr)) {
            tc::LogWindowsError(L"ReadFile(driver)", GetLastError());
            return std::nullopt;
        }
        if (read == 0) {
            break;
        }
        if (!CngSucceeded(BCryptHashData(hash.get(), buffer.data(), read, 0),
                L"BCryptHashData")) {
            return std::nullopt;
        }
    }
    std::array<UCHAR, 32> digest{};
    if (!CngSucceeded(BCryptFinishHash(hash.get(), digest.data(),
            static_cast<ULONG>(digest.size()), 0), L"BCryptFinishHash")) {
        return std::nullopt;
    }
    constexpr wchar_t digits[] = L"0123456789ABCDEF";
    std::wstring result;
    result.reserve(digest.size() * 2);
    for (const UCHAR byte : digest) {
        result += digits[byte >> 4];
        result += digits[byte & 15];
    }
    return result;
}

bool ReportFileVersion(const std::wstring& path) {
    DWORD unused = 0;
    const DWORD size = GetFileVersionInfoSizeW(path.c_str(), &unused);
    if (size == 0) {
        tc::LogWindowsError(L"GetFileVersionInfoSizeW", GetLastError());
        return false;
    }
    std::vector<BYTE> data(size);
    if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data())) {
        tc::LogWindowsError(L"GetFileVersionInfoW", GetLastError());
        return false;
    }
    void* value = nullptr;
    UINT valueLength = 0;
    if (!VerQueryValueW(data.data(), L"\\", &value, &valueLength) ||
        valueLength < sizeof(VS_FIXEDFILEINFO) || value == nullptr) {
        Log(LogLevel::Error, L"driver.version", L"Fixed version record unavailable.");
        return false;
    }
    const auto* version = static_cast<const VS_FIXEDFILEINFO*>(value);
    if (version->dwSignature != 0xFEEF04BD) {
        Log(LogLevel::Error, L"driver.version", L"Invalid fixed version signature.");
        return false;
    }
    Log(LogLevel::Information, L"driver.version",
        std::to_wstring(HIWORD(version->dwFileVersionMS)) + L"." +
        std::to_wstring(LOWORD(version->dwFileVersionMS)) + L"." +
        std::to_wstring(HIWORD(version->dwFileVersionLS)) + L"." +
        std::to_wstring(LOWORD(version->dwFileVersionLS)));
    return true;
}

bool ReadExact(HANDLE file, void* data, DWORD length) {
    DWORD read = 0;
    if (!ReadFile(file, data, length, &read, nullptr)) {
        tc::LogWindowsError(L"ReadFile(PE header)", GetLastError());
        return false;
    }
    if (read != length) {
        Log(LogLevel::Error, L"driver.pe", L"Truncated PE header.");
        return false;
    }
    return true;
}

bool ReportMachine(HANDLE file) {
    IMAGE_DOS_HEADER dos{};
    if (!Seek(file, 0) || !ReadExact(file, &dos, sizeof(dos))) {
        return false;
    }
    if (dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < static_cast<LONG>(sizeof(dos))) {
        Log(LogLevel::Error, L"driver.pe", L"Invalid DOS header.");
        return false;
    }
    DWORD signature = 0;
    IMAGE_FILE_HEADER header{};
    if (!Seek(file, dos.e_lfanew) || !ReadExact(file, &signature, sizeof(signature)) ||
        !ReadExact(file, &header, sizeof(header))) {
        return false;
    }
    if (signature != IMAGE_NT_SIGNATURE) {
        Log(LogLevel::Error, L"driver.pe", L"Invalid PE signature.");
        return false;
    }
    Log(LogLevel::Information, L"driver.machine",
        tc::HexCode(header.Machine) +
        (header.Machine == IMAGE_FILE_MACHINE_AMD64 ? L" (x64)" : L" (not x64)"));
    return true;
}

void ReportTrust(const std::wstring& path, HANDLE file) {
    Log(LogLevel::Debug, L"driver.trust.policy",
        L"Cache-only WinVerifyTrust; no UI; revocation checking disabled.");
    WINTRUST_FILE_INFO fileInfo{};
    fileInfo.cbStruct = sizeof(fileInfo);
    fileInfo.pcwszFilePath = path.c_str();
    fileInfo.hFile = file;

    WINTRUST_DATA trust{};
    trust.cbStruct = sizeof(trust);
    trust.dwUIChoice = WTD_UI_NONE;
    trust.fdwRevocationChecks = WTD_REVOKE_NONE;
    trust.dwUnionChoice = WTD_CHOICE_FILE;
    trust.pFile = &fileInfo;
    trust.dwStateAction = WTD_STATEACTION_VERIFY;
    trust.dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE;
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    const LONG result = WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE),
        &action, &trust);
    // Release the provider state before any allocation or output can throw.
    trust.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust(static_cast<HWND>(INVALID_HANDLE_VALUE), &action, &trust);

    Log(result == ERROR_SUCCESS ? LogLevel::Information : LogLevel::Warning,
        L"driver.trust", L"WinVerifyTrust=" +
        tc::HexCode(static_cast<DWORD>(result)) +
        L"; offline cached policy, revocation not checked.");
    if (result != ERROR_SUCCESS) {
        Log(LogLevel::Warning, L"driver.trust",
            L"Trust was not established by this check; this does not prove the file is unsigned.");
    }
}

bool ReportDriver(const std::wstring& path) {
    Log(LogLevel::Information, L"driver.path", path);
    // Hold the file against replacement/writes while its identity is inspected.
    tc::UniqueHandle file(CreateFileW(path.c_str(), GENERIC_READ,
        FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!file) {
        const DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
            Log(LogLevel::Warning, L"driver.file", L"Not present.");
            return true;
        }
        tc::LogWindowsError(L"CreateFileW(driver)", error);
        return false;
    }
    LARGE_INTEGER size{};
    bool complete = true;
    if (!GetFileSizeEx(file.get(), &size)) {
        tc::LogWindowsError(L"GetFileSizeEx", GetLastError());
        complete = false;
    } else {
        Log(LogLevel::Information, L"driver.bytes", std::to_wstring(size.QuadPart));
    }
    if (!ReportFileVersion(path)) {
        complete = false;
    }
    if (!ReportMachine(file.get())) {
        complete = false;
    }
    Log(LogLevel::Debug, L"driver.hash", L"Computing SHA-256 of the open file.");
    const auto hash = HashFile(file.get());
    if (!hash) {
        complete = false;
    } else {
        Log(LogLevel::Information, L"driver.sha256", *hash);
        Log(LogLevel::Information, L"driver.static-evidence",
            tc::MatchesInspectedDriver(*hash) ?
            L"Matches the inspected binary; runtime lifecycle remains unqualified." :
            L"Unrecognized binary; recorded private-interface observations do not apply.");
    }
    ReportTrust(path, file.get());
    return complete;
}

bool ReportRegistryString(const wchar_t* subkey, const wchar_t* valueName,
    std::wstring_view event) {
    DWORD bytes = 0;
    LSTATUS result = RegGetValueW(HKEY_LOCAL_MACHINE, subkey, valueName,
        RRF_RT_REG_SZ, nullptr, nullptr, &bytes);
    if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND) {
        Log(LogLevel::Information, event, L"Not present.");
        return true;
    }
    if (result != ERROR_SUCCESS) {
        tc::LogWindowsError(L"RegGetValueW(size)", static_cast<DWORD>(result));
        return false;
    }
    std::vector<wchar_t> buffer(static_cast<std::size_t>(bytes) / sizeof(wchar_t) + 1);
    result = RegGetValueW(HKEY_LOCAL_MACHINE, subkey, valueName, RRF_RT_REG_SZ,
        nullptr, buffer.data(), &bytes);
    if (result != ERROR_SUCCESS) {
        tc::LogWindowsError(L"RegGetValueW(value)", static_cast<DWORD>(result));
        return false;
    }
    Log(LogLevel::Information, event, buffer.data());
    return true;
}

bool ReportRegistryDword(const wchar_t* subkey, const wchar_t* valueName,
    std::wstring_view event) {
    DWORD value = 0;
    DWORD bytes = sizeof(value);
    const LSTATUS result = RegGetValueW(HKEY_LOCAL_MACHINE, subkey, valueName,
        RRF_RT_REG_DWORD, nullptr, &value, &bytes);
    if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND) {
        Log(LogLevel::Information, event, L"Not present.");
        return true;
    }
    if (result != ERROR_SUCCESS) {
        tc::LogWindowsError(L"RegGetValueW(DWORD)", static_cast<DWORD>(result));
        return false;
    }
    Log(LogLevel::Information, event, std::to_wstring(value));
    return true;
}

bool ReportService() {
    Log(LogLevel::Debug, L"service.query", L"Opening SCM with query-only access.");
    tc::UniqueService manager(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    if (!manager) {
        tc::LogWindowsError(L"OpenSCManagerW", GetLastError());
        return false;
    }
    tc::UniqueService service(OpenServiceW(manager.get(), L"Ramdisk",
        SERVICE_QUERY_CONFIG | SERVICE_QUERY_STATUS));
    if (!service) {
        const DWORD error = GetLastError();
        if (error == ERROR_SERVICE_DOES_NOT_EXIST) {
            Log(LogLevel::Warning, L"service.ramdisk", L"Not installed.");
            return true;
        }
        tc::LogWindowsError(L"OpenServiceW", error);
        return false;
    }
    bool complete = true;
    SERVICE_STATUS_PROCESS status{};
    DWORD bytes = 0;
    if (!QueryServiceStatusEx(service.get(), SC_STATUS_PROCESS_INFO,
        reinterpret_cast<BYTE*>(&status), sizeof(status), &bytes)) {
        tc::LogWindowsError(L"QueryServiceStatusEx", GetLastError());
        complete = false;
    } else {
        const wchar_t* state = status.dwCurrentState == SERVICE_STOPPED ? L"stopped" :
            status.dwCurrentState == SERVICE_RUNNING ? L"running" : L"transition/other";
        Log(LogLevel::Information, L"service.state",
            std::wstring(state) + L" (" + std::to_wstring(status.dwCurrentState) + L")");
        Log(LogLevel::Information, L"service.last-win32-exit",
            tc::HexCode(status.dwWin32ExitCode));
    }
    if (QueryServiceConfigW(service.get(), nullptr, 0, &bytes)) {
        Log(LogLevel::Error, L"QueryServiceConfigW", L"Unexpected empty configuration.");
        return false;
    }
    const DWORD error = GetLastError();
    if (error != ERROR_INSUFFICIENT_BUFFER || bytes < sizeof(QUERY_SERVICE_CONFIGW)) {
        tc::LogWindowsError(L"QueryServiceConfigW(size)", error);
        return false;
    }
    std::vector<BYTE> data(bytes);
    auto* config = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(data.data());
    if (!QueryServiceConfigW(service.get(), config, bytes, &bytes)) {
        tc::LogWindowsError(L"QueryServiceConfigW", GetLastError());
        return false;
    }
    Log(LogLevel::Information, L"service.start-type", std::to_wstring(config->dwStartType));
    Log(LogLevel::Information, L"service.type", tc::HexCode(config->dwServiceType));
    Log(LogLevel::Information, L"service.image-path",
        config->lpBinaryPathName != nullptr ? config->lpBinaryPathName : L"(absent)");
    return complete;
}

bool ReportEndpoint(const wchar_t* path) {
    Log(LogLevel::Debug, L"endpoint.open",
        L"Opening with zero requested data access; no IOCTL will be sent.");
    tc::UniqueHandle endpoint(CreateFileW(path, 0,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
        OPEN_EXISTING, 0, nullptr));
    if (endpoint) {
        Log(LogLevel::Information, L"endpoint", L"Opened: " + std::wstring(path));
        return true;
    }
    const DWORD error = GetLastError();
    Log(LogLevel::Information, L"endpoint",
        std::wstring(path) + L"; open failed, Win32=" + tc::HexCode(error));
    // Absence and access denial are meaningful observations, not proof of failure
    // of the native approach. Unexpected I/O failures make inspection incomplete.
    return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ||
        error == ERROR_ACCESS_DENIED || error == ERROR_SHARING_VIOLATION ||
        error == ERROR_NOT_READY || error == ERROR_DEV_NOT_EXIST;
}

bool ReportBusInterfaces() {
    tc::UniqueDeviceInfo devices(SetupDiGetClassDevsW(&tc::RamdiskBusInterface,
        nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE));
    if (!devices) {
        tc::LogWindowsError(L"SetupDiGetClassDevsW", GetLastError());
        return false;
    }
    DWORD count = 0;
    bool complete = true;
    for (;; ++count) {
        SP_DEVICE_INTERFACE_DATA interfaceData{};
        interfaceData.cbSize = sizeof(interfaceData);
        if (!SetupDiEnumDeviceInterfaces(devices.get(), nullptr,
            &tc::RamdiskBusInterface, count, &interfaceData)) {
            const DWORD error = GetLastError();
            if (error != ERROR_NO_MORE_ITEMS) {
                tc::LogWindowsError(L"SetupDiEnumDeviceInterfaces", error);
                complete = false;
            }
            break;
        }
        DWORD bytes = 0;
        SetupDiGetDeviceInterfaceDetailW(devices.get(), &interfaceData,
            nullptr, 0, &bytes, nullptr);
        const DWORD error = GetLastError();
        if (error != ERROR_INSUFFICIENT_BUFFER ||
            bytes < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)) {
            tc::LogWindowsError(L"SetupDiGetDeviceInterfaceDetailW(size)", error);
            complete = false;
            continue;
        }
        std::vector<BYTE> data(bytes);
        auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(data.data());
        detail->cbSize = sizeof(*detail);
        SP_DEVINFO_DATA node{};
        node.cbSize = sizeof(node);
        if (!SetupDiGetDeviceInterfaceDetailW(devices.get(), &interfaceData,
            detail, bytes, nullptr, &node)) {
            tc::LogWindowsError(L"SetupDiGetDeviceInterfaceDetailW", GetLastError());
            complete = false;
            continue;
        }
        Log(LogLevel::Information, L"bus.interface", detail->DevicePath);
        DWORD characters = 0;
        SetupDiGetDeviceInstanceIdW(devices.get(), &node, nullptr, 0, &characters);
        const DWORD idError = GetLastError();
        if (idError != ERROR_INSUFFICIENT_BUFFER || characters == 0) {
            tc::LogWindowsError(L"SetupDiGetDeviceInstanceIdW(size)", idError);
            complete = false;
        } else {
            std::vector<wchar_t> id(characters);
            if (SetupDiGetDeviceInstanceIdW(devices.get(), &node,
                    id.data(), characters, nullptr)) {
                Log(LogLevel::Information, L"bus.instance", id.data());
            } else {
                tc::LogWindowsError(L"SetupDiGetDeviceInstanceIdW", GetLastError());
                complete = false;
            }
        }
        if (!ReportEndpoint(detail->DevicePath)) {
            complete = false;
        }
    }
    Log(LogLevel::Information, L"bus.present-interface-count", std::to_wstring(count));
    return complete;
}

bool Inspect() {
    Log(LogLevel::Information, L"probe.mode",
        L"Read-only inspection. No driver start, private IOCTL, disk creation, or formatting.");
    SYSTEM_INFO system{};
    GetNativeSystemInfo(&system);
    Log(LogLevel::Information, L"host.native-architecture",
        std::to_wstring(system.wProcessorArchitecture) +
        (system.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_AMD64 ?
            L" (x64)" : L" (not x64)"));
    HANDLE tokenRaw = nullptr;
    bool complete = true;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tokenRaw)) {
        tc::LogWindowsError(L"OpenProcessToken", GetLastError());
        complete = false;
    } else {
        tc::UniqueHandle token(tokenRaw);
        TOKEN_ELEVATION elevation{};
        DWORD bytes = 0;
        if (!GetTokenInformation(token.get(), TokenElevation,
                &elevation, sizeof(elevation), &bytes)) {
            tc::LogWindowsError(L"GetTokenInformation", GetLastError());
            complete = false;
        } else {
            Log(LogLevel::Information, L"process.elevated",
                elevation.TokenIsElevated != 0 ? L"yes" : L"no");
        }
    }
    constexpr auto versionKey = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    constexpr auto parametersKey =
        L"SYSTEM\\CurrentControlSet\\Services\\Ramdisk\\Parameters";
    if (!ReportRegistryString(versionKey, L"CurrentBuildNumber", L"host.build")) complete = false;
    if (!ReportRegistryDword(versionKey, L"UBR", L"host.revision")) complete = false;
    if (!ReportRegistryString(versionKey, L"DisplayVersion", L"host.release")) complete = false;
    const auto directory = SystemDirectory();
    if (!directory || !ReportDriver(*directory + L"\\drivers\\ramdisk.sys")) complete = false;
    if (!ReportService()) complete = false;
    if (!ReportRegistryDword(parametersKey, L"ReportDetectedDevice",
            L"startup.ReportDetectedDevice")) complete = false;
    if (!ReportRegistryDword(parametersKey, L"Enabled", L"startup.Enabled")) complete = false;
    if (!ReportRegistryDword(parametersKey, L"DiskSize", L"startup.DiskSize")) complete = false;
    if (!ReportBusInterfaces()) complete = false;
    if (!ReportEndpoint(L"\\\\?\\GLOBALROOT\\Device\\Ramdisk")) complete = false;
    Log(LogLevel::Information, L"probe.capability",
        L"Inspection only. No driver identity has a qualified create/remove lifecycle.");
    Log(complete ? LogLevel::Information : LogLevel::Warning, L"probe.result",
        complete ? L"Inspection finished; RAM-disk usability is unproven." :
        L"Inspection incomplete; review the API diagnostics.");
    return complete;
}

void Help() {
    std::wcout <<
        L"TrueCache native-driver feasibility probe\n"
        L"  truecache-probe inspect [--verbose]\n"
        L"  truecache-probe --help\n\n"
        L"Inspects the installed Windows RAM-disk driver without starting it.\n"
        L"Creation, removal, formatting, and command execution are not implemented.\n"
        L"A matching fingerprint identifies static evidence, not supported operation.\n"
        L"Exit codes: 0 inspection/help finished; 2 invalid arguments; 3 inspection incomplete.\n";
}
}

int wmain(int argc, wchar_t* argv[]) {
    // Wide console output and UTF-8 redirected logs preserve Windows paths.
    _setmode(_fileno(stdout), _O_U8TEXT);
    _setmode(_fileno(stderr), _O_U8TEXT);
    try {
        std::vector<std::wstring_view> arguments;
        for (int i = 1; i < argc; ++i) {
            arguments.emplace_back(argv[i]);
        }
        const auto options = tc::ParseProbeArguments(arguments);
        if (options.command == tc::ProbeCommand::Help) {
            Help();
            return 0;
        }
        if (options.command == tc::ProbeCommand::Invalid) {
            std::wcerr << L"Invalid arguments. Only inspect [--verbose] and --help are supported.\n";
            return 2;
        }
        tc::SetVerboseLogging(options.verbose);
        return Inspect() ? 0 : 3;
    } catch (const std::bad_alloc&) {
        std::wcerr << L"TrueCache inspection failed: insufficient process memory.\n";
        return 3;
    } catch (const std::exception&) {
        std::wcerr << L"TrueCache inspection failed: unexpected C++ exception.\n";
        return 3;
    }
}
