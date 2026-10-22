#include "Driver.h"

static VOID RemoveDevice(_In_ PDEVICE_OBJECT DeviceObject);

#ifdef ALLOC_PRAGMA
#pragma alloc_text(INIT, DriverEntry)
#pragma alloc_text(PAGE, DriverUnload)
#pragma alloc_text(PAGE, AddDevice)
#pragma alloc_text(PAGE, RemoveDevice)
#pragma alloc_text(PAGE, GetLowerDeviceType)
#pragma alloc_text(PAGE, ReadDriverDword)
#endif

ULONG g_EmitFnAsF23 = 1;
ULONG g_GlobeHidCode = HidF23;
ULONG g_LockHidCode = HidKeyNone;

static VOID RemoveDevice(_In_ PDEVICE_OBJECT DeviceObject)
{
    PAGED_CODE();

    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    if (extension->LowerDeviceObject != NULL)
    {
        IoDetachDevice(extension->LowerDeviceObject);
    }

    IoDeleteDevice(DeviceObject);
}

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    DebugPrint("DriverEntry\n");

    DriverObject->DriverUnload = DriverUnload;
    DriverObject->DriverExtension->AddDevice = AddDevice;

    for (ULONG index = 0; index <= IRP_MJ_MAXIMUM_FUNCTION; index++)
    {
        DriverObject->MajorFunction[index] = DispatchAny;
    }

    DriverObject->MajorFunction[IRP_MJ_POWER] = DispatchPower;
    DriverObject->MajorFunction[IRP_MJ_PNP] = DispatchPnp;
    DriverObject->MajorFunction[IRP_MJ_INTERNAL_DEVICE_CONTROL] = DispatchInternalIoctl;

    ReadDriverDword(RegistryPath, L"EmitFnAsF23", &g_EmitFnAsF23);
    ReadDriverDword(RegistryPath, L"GlobeHidCode", &g_GlobeHidCode);
    ReadDriverDword(RegistryPath, L"LockHidCode", &g_LockHidCode);

    //
    // Key array slots are one byte wide; ignore values that cannot be a
    // keyboard usage rather than truncating them into a different key.
    //
    if (g_GlobeHidCode > 0xFF)
    {
        g_GlobeHidCode = HidF23;
    }

    if (g_LockHidCode > 0xFF)
    {
        g_LockHidCode = HidKeyNone;
    }

    return STATUS_SUCCESS;
}

VOID DriverUnload(_In_ PDRIVER_OBJECT DriverObject)
{
    PAGED_CODE();
    UNREFERENCED_PARAMETER(DriverObject);
    DebugPrint("DriverUnload\n");
}

NTSTATUS AddDevice(_In_ PDRIVER_OBJECT DriverObject, _In_ PDEVICE_OBJECT Pdo)
{
    PAGED_CODE();

    PDEVICE_OBJECT filterDevice = NULL;
    NTSTATUS status = IoCreateDevice(
        DriverObject,
        sizeof(DEVICE_EXTENSION),
        NULL,
        GetLowerDeviceType(Pdo),
        0,
        FALSE,
        &filterDevice);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)filterDevice->DeviceExtension;
    RtlZeroMemory(extension, sizeof(DEVICE_EXTENSION));
    IoInitializeRemoveLock(&extension->RemoveLock, 0, 0, 0);
    extension->DeviceObject = filterDevice;
    extension->Pdo = Pdo;

    PDEVICE_OBJECT lowerDevice = IoAttachDeviceToDeviceStack(filterDevice, Pdo);
    if (lowerDevice == NULL)
    {
        IoDeleteDevice(filterDevice);
        return STATUS_DEVICE_REMOVED;
    }

    extension->LowerDeviceObject = lowerDevice;
    filterDevice->Flags |= lowerDevice->Flags & (DO_DIRECT_IO | DO_BUFFERED_IO | DO_POWER_PAGABLE);
    filterDevice->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}

ULONG GetLowerDeviceType(_In_ PDEVICE_OBJECT Pdo)
{
    PAGED_CODE();

    PDEVICE_OBJECT lowerDevice = IoGetAttachedDeviceReference(Pdo);
    if (lowerDevice == NULL)
    {
        return FILE_DEVICE_UNKNOWN;
    }

    ULONG deviceType = lowerDevice->DeviceType;
    ObDereferenceObject(lowerDevice);
    return deviceType;
}

NTSTATUS CompleteRequest(_Inout_ PIRP Irp, _In_ NTSTATUS Status, _In_ ULONG_PTR Information)
{
    Irp->IoStatus.Status = Status;
    Irp->IoStatus.Information = Information;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return Status;
}

NTSTATUS DispatchAny(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    NTSTATUS status = IoAcquireRemoveLock(&extension->RemoveLock, Irp);
    if (!NT_SUCCESS(status))
    {
        return CompleteRequest(Irp, status, 0);
    }

    IoSkipCurrentIrpStackLocation(Irp);
    status = IoCallDriver(extension->LowerDeviceObject, Irp);
    IoReleaseRemoveLock(&extension->RemoveLock, Irp);
    return status;
}

NTSTATUS DispatchPower(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;

#pragma warning(suppress: 28175)
    PoStartNextPowerIrp(Irp);

    NTSTATUS status = IoAcquireRemoveLock(&extension->RemoveLock, Irp);
    if (!NT_SUCCESS(status))
    {
        return CompleteRequest(Irp, status, 0);
    }

    IoSkipCurrentIrpStackLocation(Irp);
    status = PoCallDriver(extension->LowerDeviceObject, Irp);
    IoReleaseRemoveLock(&extension->RemoveLock, Irp);
    return status;
}

NTSTATUS DispatchPnp(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);

    NTSTATUS status = IoAcquireRemoveLock(&extension->RemoveLock, Irp);
    if (!NT_SUCCESS(status))
    {
        return CompleteRequest(Irp, status, 0);
    }

    if (stack->MinorFunction == IRP_MN_REMOVE_DEVICE)
    {
        IoSkipCurrentIrpStackLocation(Irp);
        status = IoCallDriver(extension->LowerDeviceObject, Irp);
        IoReleaseRemoveLockAndWait(&extension->RemoveLock, Irp);
        RemoveDevice(DeviceObject);
        return status;
    }

    if (stack->MinorFunction == IRP_MN_DEVICE_USAGE_NOTIFICATION)
    {
        if (DeviceObject->AttachedDevice == NULL ||
            (DeviceObject->AttachedDevice->Flags & DO_POWER_PAGABLE) != 0)
        {
            DeviceObject->Flags |= DO_POWER_PAGABLE;
        }

        IoCopyCurrentIrpStackLocationToNext(Irp);
        IoSetCompletionRoutine(Irp, UsageNotificationComplete, extension, TRUE, TRUE, TRUE);
        return IoCallDriver(extension->LowerDeviceObject, Irp);
    }

    if (stack->MinorFunction == IRP_MN_START_DEVICE)
    {
        IoCopyCurrentIrpStackLocationToNext(Irp);
        IoSetCompletionRoutine(Irp, StartDeviceComplete, extension, TRUE, TRUE, TRUE);
        return IoCallDriver(extension->LowerDeviceObject, Irp);
    }

    IoSkipCurrentIrpStackLocation(Irp);
    status = IoCallDriver(extension->LowerDeviceObject, Irp);
    IoReleaseRemoveLock(&extension->RemoveLock, Irp);
    return status;
}

NTSTATUS StartDeviceComplete(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp, _In_ PVOID Context)
{
    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)Context;
    UNREFERENCED_PARAMETER(DeviceObject);

    if (Irp->PendingReturned)
    {
        IoMarkIrpPending(Irp);
    }

    if ((extension->LowerDeviceObject->Characteristics & FILE_REMOVABLE_MEDIA) != 0)
    {
        extension->DeviceObject->Characteristics |= FILE_REMOVABLE_MEDIA;
    }

    IoReleaseRemoveLock(&extension->RemoveLock, Irp);
    return STATUS_SUCCESS;
}

NTSTATUS UsageNotificationComplete(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp, _In_ PVOID Context)
{
    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)Context;
    UNREFERENCED_PARAMETER(DeviceObject);

    if (Irp->PendingReturned)
    {
        IoMarkIrpPending(Irp);
    }

    if ((extension->LowerDeviceObject->Flags & DO_POWER_PAGABLE) == 0)
    {
        extension->DeviceObject->Flags &= ~DO_POWER_PAGABLE;
    }

    IoReleaseRemoveLock(&extension->RemoveLock, Irp);
    return STATUS_SUCCESS;
}

NTSTATUS InternalIoctlComplete(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp, _In_ PVOID Context)
{
    UNREFERENCED_PARAMETER(DeviceObject);

    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)Context;
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);

    if (NT_SUCCESS(Irp->IoStatus.Status))
    {
        ULONG controlCode = stack->Parameters.DeviceIoControl.IoControlCode;
        if (controlCode == IOCTL_INTERNAL_BTH_SUBMIT_BRB)
        {
            PBRB brb = (PBRB)stack->Parameters.Others.Argument1;
            if (brb != NULL && brb->BrbHeader.Type == BRB_L2CA_ACL_TRANSFER)
            {
                PUCHAR buffer = (PUCHAR)brb->BrbL2caAclTransfer.Buffer;
                ULONG size = brb->BrbL2caAclTransfer.BufferSize;
                TryProcessAppleKeyboardTransportBuffer(buffer, size, 2);
            }
        }
        else if (controlCode == IOCTL_INTERNAL_USB_SUBMIT_URB)
        {
            PURB urb = (PURB)stack->Parameters.Others.Argument1;
            if (urb != NULL && urb->UrbHeader.Function == URB_FUNCTION_BULK_OR_INTERRUPT_TRANSFER)
            {
                PUCHAR buffer = (PUCHAR)urb->UrbBulkOrInterruptTransfer.TransferBuffer;
                if (buffer == NULL && urb->UrbBulkOrInterruptTransfer.TransferBufferMDL != NULL)
                {
                    buffer = (PUCHAR)MmGetSystemAddressForMdlSafe(
                        urb->UrbBulkOrInterruptTransfer.TransferBufferMDL,
                        NormalPagePriority | MdlMappingNoExecute);
                }

                TryProcessAppleKeyboardTransportBuffer(
                    buffer,
                    urb->UrbBulkOrInterruptTransfer.TransferBufferLength,
                    1);
            }
        }
    }

    if (Irp->PendingReturned)
    {
        IoMarkIrpPending(Irp);
    }

    IoReleaseRemoveLock(&extension->RemoveLock, Irp);
    return STATUS_SUCCESS;
}

NTSTATUS DispatchInternalIoctl(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PDEVICE_EXTENSION extension = (PDEVICE_EXTENSION)DeviceObject->DeviceExtension;
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    ULONG controlCode = stack->Parameters.DeviceIoControl.IoControlCode;
    BOOLEAN inspectOnComplete = FALSE;

    NTSTATUS status = IoAcquireRemoveLock(&extension->RemoveLock, Irp);
    if (!NT_SUCCESS(status))
    {
        return CompleteRequest(Irp, status, 0);
    }

    if (controlCode == IOCTL_INTERNAL_BTH_SUBMIT_BRB)
    {
        PBRB brb = (PBRB)stack->Parameters.Others.Argument1;
        inspectOnComplete = brb != NULL && brb->BrbHeader.Type == BRB_L2CA_ACL_TRANSFER;
    }
    else if (controlCode == IOCTL_INTERNAL_USB_SUBMIT_URB)
    {
        inspectOnComplete = TRUE;
    }

    if (inspectOnComplete)
    {
        IoCopyCurrentIrpStackLocationToNext(Irp);
        IoSetCompletionRoutine(Irp, InternalIoctlComplete, extension, TRUE, TRUE, TRUE);
        return IoCallDriver(extension->LowerDeviceObject, Irp);
    }

    IoSkipCurrentIrpStackLocation(Irp);
    status = IoCallDriver(extension->LowerDeviceObject, Irp);
    IoReleaseRemoveLock(&extension->RemoveLock, Irp);
    return status;
}

NTSTATUS ReadDriverDword(_In_ PUNICODE_STRING RegistryPath, _In_ PCWSTR ValueName, _Inout_ PULONG Value)
{
    PAGED_CODE();

    HANDLE key = NULL;
    OBJECT_ATTRIBUTES attributes;
    InitializeObjectAttributes(&attributes, RegistryPath, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

    NTSTATUS status = ZwOpenKey(&key, KEY_READ, &attributes);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    UNICODE_STRING valueName;
    RtlInitUnicodeString(&valueName, ValueName);

    UCHAR buffer[sizeof(KEY_VALUE_PARTIAL_INFORMATION) + sizeof(ULONG)] = { 0 };
    ULONG resultLength = 0;
    status = ZwQueryValueKey(
        key,
        &valueName,
        KeyValuePartialInformation,
        buffer,
        sizeof(buffer),
        &resultLength);

    if (NT_SUCCESS(status))
    {
        PKEY_VALUE_PARTIAL_INFORMATION value = (PKEY_VALUE_PARTIAL_INFORMATION)buffer;
        if (value->Type == REG_DWORD && value->DataLength == sizeof(ULONG))
        {
            RtlCopyMemory(Value, value->Data, sizeof(ULONG));
        }
    }

    ZwClose(key);
    return status;
}
