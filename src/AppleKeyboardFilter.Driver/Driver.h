#pragma once

#include <ntddk.h>
#include <wdm.h>
#include <initguid.h>
#include <ntstrsafe.h>
#include <bthdef.h>
#include <ntintsafe.h>
#include <bthguid.h>
#include <bthioctl.h>
#include <sdpnode.h>
#include <bthddi.h>
#include <bthsdpddi.h>
#include <bthsdpdef.h>
#include "usbioctl.h"
#include "usbdi.h"

#ifdef __cplusplus
extern "C" {
#endif

#define DRIVERNAME "AppleKeyboardFilter"

#if DBG
#define DebugPrint(...) DbgPrint(DRIVERNAME ": " __VA_ARGS__)
#else
#define DebugPrint(...)
#endif

extern ULONG g_EmitFnAsF23;
extern ULONG g_GlobeHidCode;
extern ULONG g_LockHidCode;

enum AppleKeyboardHidCodes
{
    HidKeyNone = 0x00,
    HidF23 = 0x72,
};

enum AppleKeyboardHidMasks
{
    AppleSpecialFnMask = 0x02,
    AppleSpecialLockMask = 0x08,
};

typedef struct _DEVICE_EXTENSION
{
    PDEVICE_OBJECT DeviceObject;
    PDEVICE_OBJECT LowerDeviceObject;
    PDEVICE_OBJECT Pdo;
    IO_REMOVE_LOCK RemoveLock;
} DEVICE_EXTENSION, *PDEVICE_EXTENSION;

DRIVER_INITIALIZE DriverEntry;
DRIVER_UNLOAD DriverUnload;
DRIVER_ADD_DEVICE AddDevice;

NTSTATUS DispatchAny(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp);
NTSTATUS DispatchPower(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp);
NTSTATUS DispatchPnp(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp);
NTSTATUS DispatchInternalIoctl(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp);
NTSTATUS InternalIoctlComplete(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp, _In_ PVOID Context);
NTSTATUS StartDeviceComplete(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp, _In_ PVOID Context);
NTSTATUS UsageNotificationComplete(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp, _In_ PVOID Context);

NTSTATUS CompleteRequest(_Inout_ PIRP Irp, _In_ NTSTATUS Status, _In_ ULONG_PTR Information);
ULONG GetLowerDeviceType(_In_ PDEVICE_OBJECT Pdo);
NTSTATUS ReadDriverDword(_In_ PUNICODE_STRING RegistryPath, _In_ PCWSTR ValueName, _Inout_ PULONG Value);
VOID ProcessAppleKeyboardReport(_Inout_updates_bytes_(Size) PUCHAR Report, _In_ ULONG Size);
BOOLEAN TryProcessAppleKeyboardTransportBuffer(_Inout_updates_bytes_(Size) PUCHAR Buffer, _In_ ULONG Size, _In_ ULONG PrefixBytes);

#ifdef __cplusplus
}
#endif
