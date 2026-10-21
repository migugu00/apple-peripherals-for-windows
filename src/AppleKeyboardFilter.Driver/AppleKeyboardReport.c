#include "Driver.h"

VOID ProcessAppleKeyboardReport(_Inout_updates_bytes_(Size) PUCHAR Report, _In_ ULONG Size)
{
    if (Report == NULL || Size < 9 || !g_EmitFnAsF23)
    {
        return;
    }

    PUCHAR modifiers = &Report[1];
    PUCHAR keySlots = &Report[2];
    PUCHAR specialKey = &Report[8];
    UCHAR mask = (UCHAR)AppleSpecialFnMask;

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

    if ((*specialKey & mask) == 0)
    {
        DebugPrint(
            "AKF: report pre=[%02x %02x %02x %02x %02x %02x %02x %02x %02x]\n",
            pre[0], pre[1], pre[2], pre[3], pre[4], pre[5], pre[6], pre[7], pre[8]);
        return;
    }

    DebugPrint(
        "AKF: fn-report pre=[%02x %02x %02x %02x %02x %02x %02x %02x %02x]\n",
        pre[0], pre[1], pre[2], pre[3], pre[4], pre[5], pre[6], pre[7], pre[8]);

    //
    // The keyboard's 6KRO key array declares usages 0x00..0x65 only, so any
    // injected F13..F24 keycode (0x68..0x73) is silently dropped by the HID
    // parser. The modifier bitmap (E0..E7) IS declared, so while fn/globe is
    // held we OR synthetic modifier bits into the modifier byte instead.
    //
    UCHAR modifierMask = (UCHAR)g_FnModifierMask;
    if (modifierMask != 0)
    {
        *modifiers |= modifierMask;
        DebugPrint("AKF: Fn key held -> modifier mask 0x%02x\n", modifierMask);
    }

    UCHAR emitCode = (UCHAR)g_EmitHidCode;
    if (emitCode == 0)
    {
        return;
    }

    BOOLEAN alreadyPresent = FALSE;
    for (ULONG index = 0; index < 6; index++)
    {
        if (keySlots[index] == emitCode)
        {
            alreadyPresent = TRUE;
            break;
        }
    }

    if (!alreadyPresent)
    {
        for (ULONG index = 0; index < 6; index++)
        {
            if (keySlots[index] == HidKeyNone)
            {
                keySlots[index] = emitCode;
                break;
            }
        }
    }

    DebugPrint("AKF: patched Fn key bit -> emit code 0x%02x\n", emitCode);

    *specialKey &= ~mask;
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
            " special=0x%02x mod=0x%02x mask=0x%02x emit=0x%02x",
            report[8],
            report[1],
            (UCHAR)g_FnModifierMask,
            (UCHAR)g_EmitHidCode);
    }
    else
    {
        DbgPrint(" body-too-short(%lu)", Size - PrefixBytes);
    }
    DbgPrint("\n");
}
