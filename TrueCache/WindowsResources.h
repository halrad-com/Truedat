#pragma once
#include <windows.h>
#include <winsvc.h>
#include <bcrypt.h>
#include <setupapi.h>
#include <utility>

namespace tc {
template<class Traits>
class UniqueResource {
public:
    using value_type = typename Traits::value_type;
    explicit UniqueResource(value_type value = Traits::empty()) noexcept
        : value_(value) {}
    ~UniqueResource() noexcept { reset(); }
    UniqueResource(const UniqueResource&) = delete;
    UniqueResource& operator=(const UniqueResource&) = delete;
    UniqueResource(UniqueResource&& other) noexcept
        : value_(std::exchange(other.value_, Traits::empty())) {}
    UniqueResource& operator=(UniqueResource&& other) noexcept {
        if (this != &other) {
            reset(std::exchange(other.value_, Traits::empty()));
        }
        return *this;
    }
    value_type get() const noexcept { return value_; }
    explicit operator bool() const noexcept { return Traits::valid(value_); }
    void reset(value_type value = Traits::empty()) noexcept {
        if (Traits::valid(value_)) {
            Traits::close(value_);
        }
        value_ = value;
    }
private:
    value_type value_;
};

struct HandleTraits {
    using value_type = HANDLE;
    static HANDLE empty() noexcept { return nullptr; }
    static bool valid(HANDLE value) noexcept {
        return value != nullptr && value != INVALID_HANDLE_VALUE;
    }
    static void close(HANDLE value) noexcept { CloseHandle(value); }
};
struct ServiceTraits {
    using value_type = SC_HANDLE;
    static SC_HANDLE empty() noexcept { return nullptr; }
    static bool valid(SC_HANDLE value) noexcept { return value != nullptr; }
    static void close(SC_HANDLE value) noexcept { CloseServiceHandle(value); }
};
struct DeviceInfoTraits {
    using value_type = HDEVINFO;
    static HDEVINFO empty() noexcept { return INVALID_HANDLE_VALUE; }
    static bool valid(HDEVINFO value) noexcept {
        return value != nullptr && value != INVALID_HANDLE_VALUE;
    }
    static void close(HDEVINFO value) noexcept { SetupDiDestroyDeviceInfoList(value); }
};
struct AlgorithmTraits {
    using value_type = BCRYPT_ALG_HANDLE;
    static BCRYPT_ALG_HANDLE empty() noexcept { return nullptr; }
    static bool valid(BCRYPT_ALG_HANDLE value) noexcept { return value != nullptr; }
    static void close(BCRYPT_ALG_HANDLE value) noexcept {
        BCryptCloseAlgorithmProvider(value, 0);
    }
};
struct HashTraits {
    using value_type = BCRYPT_HASH_HANDLE;
    static BCRYPT_HASH_HANDLE empty() noexcept { return nullptr; }
    static bool valid(BCRYPT_HASH_HANDLE value) noexcept { return value != nullptr; }
    static void close(BCRYPT_HASH_HANDLE value) noexcept { BCryptDestroyHash(value); }
};
using UniqueHandle = UniqueResource<HandleTraits>;
using UniqueService = UniqueResource<ServiceTraits>;
using UniqueDeviceInfo = UniqueResource<DeviceInfoTraits>;
using UniqueAlgorithm = UniqueResource<AlgorithmTraits>;
using UniqueHash = UniqueResource<HashTraits>;
}
