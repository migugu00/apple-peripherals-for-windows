#include "Driver.h"

//
// Set while our proxy key is present in the last patched report, so we know
// to remove it from the key array once the Globe/special bit disappears.
//
static BOOLEAN g_ProxyInserted = FALSE;

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

    UCHAR proxyCode = (UCHAR)g_ProxyHidCode;

    if ((*specialKey & mask) == 0)
    {
        //
        // Globe no longer held: scrub any injected proxy key out of the key
        // array so the HID parser emits a proper break for it (and the
        // user-mode helper releases its synthetic Ctrl).
        //
        if (g_ProxyInserted && proxyCode != 0)
        {
            for (ULONG index = 0; index < 6; index++)
            {
                if (keySlots[index] == proxyCode)
                {
                    keySlots[index] = HidKeyNone;
                }
            }
            g_ProxyInserted = FALSE;
            DebugPrint("AKF: Globe released -> proxy key 0x%02x removed\n", proxyCode);
        }

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
    // parser. While fn/globe is held, inject a proxy keycode (default 0x64,
    // "Non-US \\ |" -> VK_OEM_102) into an empty key slot; a user-mode helper
    // translates that VK into LeftCtrl and swallows it.
    //
    UCHAR modifierMask = (UCHAR)g_FnModifierMask;
    if (modifierMask != 0)
    {
        *modifiers |= modifierMask;
        DebugPrint("AKF: Fn key held -> modifier mask 0x%02x\n", modifierMask);
    }

    if (proxyCode == 0)
    {
        return;
    }

    BOOLEAN alreadyPresent = FALSE;
    for (ULONG index = 0; index < 6; index++)
    {
        if (keySlots[index] == proxyCode)
        {
            alreadyPresent = TRUE;
            break;
        }
    }

    if (!alreadyPresent)
    {
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
                keySlots[index] = proxyCode;
                g_ProxyInserted = TRUE;
                DebugPrint("AKF: Fn held -> proxy key 0x%02x in slot %lu\n", proxyCode, (ULONG)index);
                break;
            }
        }
    }

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
            " special=0x%02x mod=0x%02x mask=0x%02x proxy=0x%02x",
            report[8],
            report[1],
            (UCHAR)g_FnModifierMask,
            (UCHAR)g_ProxyHidCode);
    }
    else
    {
        DbgPrint(" body-too-short(%lu)", Size - PrefixBytes);
    }
    DbgPrint("\n");
}
