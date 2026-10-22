#include "Driver.h"

static VOID InsertKeyboardUsage(_Inout_updates_(6) PUCHAR KeySlots, _In_ UCHAR Usage)
{
    for (ULONG index = 0; index < 6; index++)
    {
        if (KeySlots[index] == Usage)
        {
            return;
        }
    }

    for (ULONG index = 0; index < 6; index++)
    {
        if (KeySlots[index] == HidKeyNone)
        {
            KeySlots[index] = Usage;
            return;
        }
    }
}

VOID ProcessAppleKeyboardReport(_Inout_updates_bytes_(Size) PUCHAR Report, _In_ ULONG Size)
{
    if (Report == NULL || Size < 9)
    {
        return;
    }

    //
    // Every report is a fresh snapshot of the keys held right now, so the
    // emitted usage only needs adding while its special bit is set. It drops
    // out of the next report on its own once the key is released.
    //
    PUCHAR keySlots = &Report[2];
    PUCHAR specialKey = &Report[8];

    if (g_EmitFnAsF23 && g_GlobeHidCode != HidKeyNone && (*specialKey & AppleSpecialFnMask) != 0)
    {
        InsertKeyboardUsage(keySlots, (UCHAR)g_GlobeHidCode);
        *specialKey &= ~AppleSpecialFnMask;
    }

    if (g_LockHidCode != HidKeyNone && (*specialKey & AppleSpecialLockMask) != 0)
    {
        InsertKeyboardUsage(keySlots, (UCHAR)g_LockHidCode);
        *specialKey &= ~AppleSpecialLockMask;
    }
}

BOOLEAN TryProcessAppleKeyboardTransportBuffer(_Inout_updates_bytes_(Size) PUCHAR Buffer, _In_ ULONG Size, _In_ ULONG PrefixBytes)
{
    const ULONG keyboardReportBodyLength = 9;
    if (Buffer == NULL || Size < PrefixBytes + keyboardReportBodyLength)
    {
        return FALSE;
    }

    ProcessAppleKeyboardReport(Buffer + PrefixBytes, keyboardReportBodyLength);
    return TRUE;
}
