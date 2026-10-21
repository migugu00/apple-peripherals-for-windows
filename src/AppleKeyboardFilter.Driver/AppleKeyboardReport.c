#include "Driver.h"

VOID ProcessAppleKeyboardReport(_Inout_updates_bytes_(Size) PUCHAR Report, _In_ ULONG Size)
{
    if (Report == NULL || Size < 9 || !g_EmitFnAsF23)
    {
        return;
    }

    PUCHAR keySlots = &Report[2];
    PUCHAR specialKey = &Report[8];
    UCHAR mask = (UCHAR)AppleSpecialFnMask;
    if ((*specialKey & mask) == 0)
    {
        return;
    }

    UCHAR emitCode = (UCHAR)g_EmitHidCode;

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
        DbgPrint(" special=0x%02x emit=0x%02x", report[8], (UCHAR)g_EmitHidCode);
    }
    else
    {
        DbgPrint(" body-too-short(%lu)", Size - PrefixBytes);
    }
    DbgPrint("\n");
}
