#include "Diagnostics.h"
#include <iomanip>
#include <iostream>
#include <iterator>
#include <sstream>

namespace tc {
namespace {
bool verbose = false;
std::wstring Escape(std::wstring_view input) {
    std::wstring output;
    for (const wchar_t ch : input) {
        switch (ch) {
        case L'\r': output += L"\\r"; break;
        case L'\n': output += L"\\n"; break;
        case L'\t': output += L"\\t"; break;
        default: output += ch; break;
        }
    }
    return output;
}
}
void SetVerboseLogging(bool enabled) noexcept { verbose = enabled; }

void Log(LogLevel level, std::wstring_view event, std::wstring_view detail) {
    if (!verbose && (level == LogLevel::Trace || level == LogLevel::Debug)) {
        return;
    }
    const wchar_t* name = L"Information";
    switch (level) {
    case LogLevel::Trace: name = L"Trace"; break;
    case LogLevel::Debug: name = L"Debug"; break;
    case LogLevel::Warning: name = L"Warning"; break;
    case LogLevel::Error: name = L"Error"; break;
    case LogLevel::Information: break;
    }
    SYSTEMTIME time{};
    GetSystemTime(&time);
    std::wostringstream line;
    line << std::setfill(L'0') << std::setw(4) << time.wYear << L'-'
        << std::setw(2) << time.wMonth << L'-' << std::setw(2) << time.wDay
        << L'T' << std::setw(2) << time.wHour << L':' << std::setw(2) << time.wMinute
        << L':' << std::setw(2) << time.wSecond << L'Z'
        << L" [" << name << L"] " << Escape(event) << L": " << Escape(detail) << L'\n';
    auto& stream = level == LogLevel::Error ? std::wcerr : std::wcout;
    stream << line.str();
}

std::wstring HexCode(DWORD code) {
    std::wostringstream text;
    text << L"0x" << std::uppercase << std::hex << std::setfill(L'0')
         << std::setw(8) << code;
    return text.str();
}

void LogWindowsError(std::wstring_view api, DWORD error) {
    wchar_t message[1024]{};
    const DWORD length = FormatMessageW(
        FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, 0, message, static_cast<DWORD>(std::size(message)), nullptr);
    std::wstring detail = L"Win32=" + HexCode(error);
    if (length != 0) {
        std::wstring description(message, length);
        while (!description.empty() &&
            (description.back() == L'\r' || description.back() == L'\n')) {
            description.pop_back();
        }
        detail += L" " + description;
    }
    Log(LogLevel::Error, api, detail);
}
}
