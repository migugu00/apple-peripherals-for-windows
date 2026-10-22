#include "Driver.h"

//
// Insertion-state flags: set while our proxy keys are present in the last
// patched report, so we know to remove them from the key array once their
// special bit disappears.
//
static BOOLEAN g_GlobeProxyInserted = FALSE;
static BOOLEAN g_LockProxyInserted = FALSE;

static VOID EnsureProxyAbsent(_In_ PUCHAR keySlots, _In_ UCHAR code, _Inout_ PBOOLEAN inserted)
{
    if (!*inserted)
    {
        return;
    }

    for (ULONG index = 0; index < 6; index++)
    {
        if (keySlots[index] == code)
        {
            keySlots[index] = HidKeyNone;
        }
    }

    *inserted = FALSE;
}

static VOID EnsureProxyPresent(_In_ PUCHAR keySlots, _In_ UCHAR code, _Inout_ PBOOLEAN inserted)
{
    if (code == 0)
    {
        return;
    }

    for (ULONG index = 0; index < 6; index++)
    {
        if (keySlots[index] == code)
        {
            *inserted = TRUE;
            return;
        }
    }

    //
    // Insert into the LAST empty slot: real keys fill slots 0,1,2... from
    // the keyboard firmware, so a late slot keeps our proxy position
    // stable and avoids a break/make flicker when a real key lands in a
    // slot the proxy previously occupied.
    //
    for (LONG index = 5; index >= 0; index--)
    {
        if (keySlots[index] == HidKeyNone)
        {
            keySlots[index] = code;
            *inserted = TRUE;
            return;
        }
    }
}

VOID ProcessAppleKeyboardReport(_Inout_updates_bytes_(Size) PUCHAR Report, _In_ ULONG Size)
{
    if (Report == NULL || Size < 9 || !g_EmitFnAsF23)
    {
        return;
    }

    PUCHAR modifiers = &Report[1];
    PUCHAR keySlots = &Report[2];
    PUCHAR specialKey = &Report[8];

    UCHAR pre[9];
    RtlCopyMemory(pre, Report, 9);

    //
    // Delivery probe: when ForceModifierMask is set, OR it into the modifier
    // byte on EVERY keyboard report so a stuck-modifier symptom proves the
    // patched buffer reaches the HID parser.
    //
    if (g_ForceModifierMask != 0)
    {
        *modifiers |= (UCHAR)g_ForceModifierMask;
        DebugPrint(
            "AKF: force pre=[%02x %02x %02x %02x %02x %02x %02x %02x %02x]\n",
            pre[0], pre[1], pre[2], pre[3], pre[4], pre[5], pre[6], pre[7], pre[8]);
    }

    //
    // The keyboard's 6KRO key array declares usages 0x00..0x65 only, so any
    // injected F13..F24 keycode (0x68..0x73) is silently dropped by the HID
    // parser. Vendor-only special keys (Globe/Fn bit 0x02, Lock bit 0x08 in
    // the special byte) are therefore remapped by injecting proxy keyboard
    // usages into an empty key slot; a user-mode helper translates those
    // VKs into real input and swallows the proxy keys:
    //
    //   Globe -> ProxyHidCode     (default 0x64, Non-US \| -> VK_OEM_102 -> helper sends LeftCtrl)
    //   Lock  -> LockProxyHidCode (default 0x56, Keypad -  -> VK_SUBTRACT  -> helper sends VK_DELETE)
    //
    BOOLEAN globeHeld = (*specialKey & (UCHAR)AppleSpecialFnMask) != 0;
    BOOLEAN lockHeld = (*specialKey & (UCHAR)AppleSpecialLockMask) != 0;

    UCHAR globeWanted = globeHeld ? (UCHAR)g_ProxyHidCode : 0;
    UCHAR lockWanted = lockHeld ? (UCHAR)g_LockProxyHidCode : 0;

    if (!globeHeld && g_GlobeProxyInserted)
    {
        EnsureProxyAbsent(keySlots, (UCHAR)g_ProxyHidCode, &g_GlobeProxyInserted);
        DebugPrint("AKF: Globe released -> proxy key 0x%02x removed\n", (UCHAR)g_ProxyHidCode);
    }
    else if (globeHeld && globeWanted != 0)
    {
        BOOLEAN wasPresent = g_GlobeProxyInserted;
        EnsureProxyPresent(keySlots, globeWanted, &g_GlobeProxyInserted);
        if (!wasPresent)
        {
            DebugPrint("AKF: Globe held -> proxy key 0x%02x inserted\n", globeWanted);
        }
    }

    if (!lockHeld && g_LockProxyInserted)
    {
        EnsureProxyAbsent(keySlots, (UCHAR)g_LockProxyHidCode, &g_LockProxyInserted);
        DebugPrint("AKF: Lock released -> proxy key 0x%02x removed\n", (UCHAR)g_LockProxyHidCode);
    }
    else if (lockHeld && lockWanted != 0)
    {
        BOOLEAN wasPresent = g_LockProxyInserted;
        EnsureProxyPresent(keySlots, lockWanted, &g_LockProxyInserted);
        if (!wasPresent)
        {
            DebugPrint("AKF: Lock held -> proxy key 0x%02x inserted\n", lockWanted);
        }
    }

    //
    // Optional legacy knob: OR global modifier bits while Globe is held.
    //
    USHORT modifierMask = (USHORT)(g_FnModifierMask & 0xFFFF);
    if (modifierMask != 0 && globeHeld)
    {
        *modifiers |= modifierMask;
        DebugPrint("AKF: Fn key held -> modifier mask 0x%02x\n", modifierMask);
    }

    //
    // Consume the vendor special bits we handled so they do not leak
    // downstream as unknown vendor events.
    //
    *specialKey &= (UCHAR)~(AppleSpecialFnMask | AppleSpecialLockMask);

    DebugPrint(
        "AKF: report pre=[%02x %02x %02x %02x %02x %02x %02x %02x %02x]\n",
        pre[0], pre[1], pre[2], pre[3], pre[4], pre[5], pre[6], pre[7], pre[8]);
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

VOID TraceAppleTransportBuffer(_In_opt_ PUCHAR Buffer, _In_ ULONG Size, _In_ ULONG PrefixBytes)
{
#if AKF_TRACE_ON == 0
    UNREFERENCED_PARAMETER(Buffer);
    UNREFERENCED_PARAMETER(Size);
    UNREFERENCED_PARAMETER(PrefixBytes);
#else
    if (Buffer == NULL)
    {
        DbgPrint("AKF: size=%lu prefix=%lu buffer=NULL\n", Size, PrefixBytes);
        return;
    }

    ULONG shown = Size < 16 ? Size : 16;
    DbgPrint("AKF: size=%lu prefix=%lu data=", Size, PrefixBytes);
    for (ULONG index = 0; index < shown; index++)
    {
        DbgPrint("%02x", Buffer[index]);
    }

    if (Size >= PrefixBytes + 9)
    {
        PUCHAR report = Buffer + PrefixBytes;
        DbgPrint(
            " special=0x%02x mod=0x%02x proxy=0x%02x lock=0x%02x",
            report[8],
            report[1],
            (UCHAR)g_ProxyHidCode,
            (UCHAR)g_LockProxyHidCode);
    }
    else
    {
        DbgPrint(" body-too-short(%lu)", Size - PrefixBytes);
    }
    DbgPrint("\n");
#endif
}
