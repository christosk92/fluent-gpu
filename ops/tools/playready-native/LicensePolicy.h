// LicensePolicy.h — the license lifecycle's pure decisions, pulled out of PrLicense.cpp so a dependency-free test exe
// (tests/FeedTests.cpp) can exercise them with no <windows.h>, no CDM and no clock: which MF_MEDIAKEY_STATUS values mean
// what, what a batch of key statuses does to a license in a given state, and when a Pending license is stale.
//
// WHY. The first cut only understood USABLE and EXPIRED. A key that later went INTERNAL_ERROR / RELEASED /
// OUTPUT_NOT_ALLOWED (a hardware-context reset, a failed output-protection monitor, a CDM that dropped it) stayed
// "usable" in the cache and was handed to every new attach, which then decrypted nothing. A Pending license whose only
// status was one of those, or whose CDM took the license and never reported a status at all, stayed Pending for ever and
// every retry joined it. Chromium maps all eight statuses (ToCdmKeyStatus); this does the same for the three that kill a
// key, reports the two output restrictions as their own event, and bounds how long Pending may last.
#pragma once

#include <cstdint>

namespace fgpr::licensing {

// MF_MEDIAKEY_STATUS, restated so this header needs no Media Foundation include (values pinned by mfidl.h).
constexpr int32_t kStatusUsable = 0;
constexpr int32_t kStatusExpired = 1;
constexpr int32_t kStatusOutputDownscaled = 2;
constexpr int32_t kStatusOutputNotAllowed = 3;
constexpr int32_t kStatusPending = 4;
constexpr int32_t kStatusInternalError = 5;
constexpr int32_t kStatusReleased = 6;
constexpr int32_t kStatusOutputRestricted = 7;

/// How long a license may stay Pending (a challenge in flight, or a CDM that never reported a status) before it is
/// STALE: the next FgPrLicenseAcquire for its KID replaces it instead of joining it. Below the session's start budget
/// (10 s) so a user retry of an open that just failed at "Licensing" does not join the attempt that failed it. The
/// managed cache (LicenseCachePolicy.PendingDeadlineMs) uses the same number.
constexpr int64_t kPendingDeadlineMs = 8000;

/// Whether a license that has been Pending for `ageMs` is stale.
inline bool PendingStale(int64_t ageMs) { return ageMs >= kPendingDeadlineMs; }

/// The failing HRESULT a dead key status becomes: 0x8004800N with N = the MF_MEDIAKEY_STATUS value. A private code in
/// the ITF range (no real DRM_E_* collides with it) so a log line says which status killed the key.
inline int32_t DeadHr(int32_t status) { return (int32_t)(0x80048000u + (uint32_t)status); }

/// True for the statuses after which the key will never decrypt again.
inline bool IsDeadStatus(int32_t status)
{
    return status == kStatusInternalError || status == kStatusReleased || status == kStatusOutputNotAllowed;
}

/// What one GetKeyStatuses() batch says, folded: one entry per key the session holds.
struct KeyStatusSummary
{
    bool usable = false;
    bool expired = false;
    int32_t deadStatus = -1;     // the first dead status seen (INTERNAL_ERROR / RELEASED / OUTPUT_NOT_ALLOWED), else -1
    int32_t restriction = 0;     // OUTPUT_RESTRICTED beats OUTPUT_DOWNSCALED; 0 = unrestricted

    void Add(int32_t status)
    {
        if (status == kStatusUsable) usable = true;
        else if (status == kStatusExpired) expired = true;
        else if (IsDeadStatus(status)) { if (deadStatus < 0) deadStatus = status; }
        else if (status == kStatusOutputRestricted) restriction = kStatusOutputRestricted;
        else if (status == kStatusOutputDownscaled) { if (restriction == 0) restriction = kStatusOutputDownscaled; }
        // STATUS_PENDING (and any value a newer OS adds) is "not decided yet": never an error.
    }
};

enum class KeyAction
{
    None,
    BecomeUsable,    // Pending / Expired -> Usable
    BecomeExpired,   // Pending / Usable -> Expired
    Kill,            // Pending / Usable -> dead: fail the license, raise it, evict it so the next acquire re-issues
};

/// What `s` does to a license whose state is `state` (FgPrLicenseState: 0 pending, 1 usable, 2 expired, < 0 failed).
/// A USABLE key wins over a dead one (a license can carry several keys; the KID this session was opened for decrypts
/// while any key is usable). A license already failed stays failed, and an expired one is only revived by USABLE.
inline KeyAction NextAction(const KeyStatusSummary& s, int32_t state)
{
    if (state < 0) return KeyAction::None;
    if (s.usable) return state == 1 ? KeyAction::None : KeyAction::BecomeUsable;
    if (state == 2) return KeyAction::None;
    if (s.deadStatus >= 0) return KeyAction::Kill;
    if (s.expired) return KeyAction::BecomeExpired;
    return KeyAction::None;
}

}   // namespace fgpr::licensing
