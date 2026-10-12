#pragma once
#include <windows.h>
#include <string>
#include <string_view>

namespace tc {
enum class LogLevel { Trace, Debug, Information, Warning, Error };
void SetVerboseLogging(bool enabled) noexcept;
void Log(LogLevel level, std::wstring_view event, std::wstring_view detail);
std::wstring HexCode(DWORD code);
void LogWindowsError(std::wstring_view api, DWORD error);
}
