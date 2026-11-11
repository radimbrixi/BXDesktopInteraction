#include <ntddk.h>
#include <wdf.h>
#include <hidport.h>
#include <vhf.h>
#include "Public.h"

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD BxDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL BxDeviceControl;
EVT_WDF_OBJECT_CONTEXT_CLEANUP BxCleanup;
EVT_WDF_FILE_CLEANUP BxFileCleanup;
typedef struct _BX_CONTEXT { VHFHANDLE Vhf; WDFWAITLOCK Lock; } BX_CONTEXT;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(BX_CONTEXT, BxContext)

// Relative five-button mouse; X/Y signed 16 bit, wheels signed 8 bit.
static const UCHAR Descriptor[] = {
    0x05,0x01, 0x09,0x02, 0xA1,0x01, 0x85,0x01,
    0x09,0x01, 0xA1,0x00,
    0x05,0x09, 0x19,0x01, 0x29,0x05, 0x15,0x00, 0x25,0x01,
    0x75,0x01, 0x95,0x05, 0x81,0x02,
    0x75,0x03, 0x95,0x01, 0x81,0x03,
    0x05,0x01, 0x09,0x30, 0x09,0x31,
    0x16,0x01,0x80, 0x26,0xFF,0x7F, 0x75,0x10, 0x95,0x02, 0x81,0x06,
    0x09,0x38, 0x15,0x81, 0x25,0x7F, 0x75,0x08, 0x95,0x01, 0x81,0x06,
    0x05,0x0C, 0x0A,0x38,0x02, 0x15,0x81, 0x25,0x7F,
    0x75,0x08, 0x95,0x01, 0x81,0x06, 0xC0,0xC0
};

NTSTATUS DriverEntry(PDRIVER_OBJECT driver, PUNICODE_STRING registry)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, BxDeviceAdd);
    return WdfDriverCreate(driver, registry, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS BxDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT init)
{
    UNREFERENCED_PARAMETER(driver);
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queue;
    WDF_FILEOBJECT_CONFIG file;
    VHF_CONFIG vhf;
    DECLARE_CONST_UNICODE_STRING(name, L"\\Device\\BxVirtualMouse");
    DECLARE_CONST_UNICODE_STRING(link, L"\\DosDevices\\BxVirtualMouse");
    DECLARE_CONST_UNICODE_STRING(security, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    WdfDeviceInitSetDeviceType(init, FILE_DEVICE_UNKNOWN);
    WdfDeviceInitSetCharacteristics(init, FILE_DEVICE_SECURE_OPEN, FALSE);
    WdfDeviceInitSetExclusive(init, TRUE);
    status = WdfDeviceInitAssignName(init, &name);
    if (!NT_SUCCESS(status)) return status;
    status = WdfDeviceInitAssignSDDLString(init, &security);
    if (!NT_SUCCESS(status)) return status;
    WDF_FILEOBJECT_CONFIG_INIT(&file, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, BxFileCleanup);
    WdfDeviceInitSetFileObjectConfig(init, &file, WDF_NO_OBJECT_ATTRIBUTES);
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, BX_CONTEXT);
    attributes.EvtCleanupCallback = BxCleanup;
    attributes.ExecutionLevel = WdfExecutionLevelPassive;
    status = WdfDeviceCreate(&init, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;
    BX_CONTEXT* context = BxContext(device);
    WDF_OBJECT_ATTRIBUTES lockAttributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = device;
    status = WdfWaitLockCreate(&lockAttributes, &context->Lock);
    if (!NT_SUCCESS(status)) return status;
    VHF_CONFIG_INIT(&vhf, WdfDeviceWdmGetDeviceObject(device), sizeof(Descriptor), (PUCHAR)Descriptor);
    // Prototype identifiers only; obtain assigned IDs before distributing hardware/software.
    vhf.VendorID = 0xFEED;
    vhf.ProductID = 0xB001;
    vhf.VersionNumber = 1;
    status = VhfCreate(&vhf, &context->Vhf);
    if (!NT_SUCCESS(status)) return status;
    status = VhfStart(context->Vhf);
    if (!NT_SUCCESS(status)) return status;
    status = WdfDeviceCreateSymbolicLink(device, &link);
    if (!NT_SUCCESS(status)) return status;
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queue, WdfIoQueueDispatchSequential);
    queue.EvtIoDeviceControl = BxDeviceControl;
    return WdfIoQueueCreate(device, &queue, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
}

static NTSTATUS Submit(BX_CONTEXT* context, PBX_MOUSE_REPORT report)
{
    HID_XFER_PACKET packet = {0};
    packet.reportBuffer = (PUCHAR)report;
    packet.reportBufferLen = sizeof(*report);
    packet.reportId = 1;
    WdfWaitLockAcquire(context->Lock, NULL);
    NTSTATUS status = context->Vhf ? VhfReadReportSubmit(context->Vhf, &packet) : STATUS_DEVICE_NOT_READY;
    WdfWaitLockRelease(context->Lock);
    return status;
}

VOID BxDeviceControl(WDFQUEUE queue, WDFREQUEST request, size_t outputLength, size_t inputLength, ULONG code)
{
    UNREFERENCED_PARAMETER(outputLength);
    PBX_MOUSE_REPORT report;
    NTSTATUS status;
    if (code != IOCTL_BX_MOUSE_REPORT) { WdfRequestComplete(request, STATUS_INVALID_DEVICE_REQUEST); return; }
    if (inputLength != sizeof(BX_MOUSE_REPORT)) { WdfRequestComplete(request, STATUS_INVALID_BUFFER_SIZE); return; }
    status = WdfRequestRetrieveInputBuffer(request, sizeof(BX_MOUSE_REPORT), (PVOID*)&report, NULL);
    if (NT_SUCCESS(status)) {
        if (report->ReportId != 1 || (report->Buttons & ~0x1F) || report->X == -32768 || report->Y == -32768 || report->Wheel == -128 || report->HorizontalWheel == -128)
            status = STATUS_INVALID_PARAMETER;
        else status = Submit(BxContext(WdfIoQueueGetDevice(queue)), report);
    }
    WdfRequestComplete(request, status);
}

VOID BxFileCleanup(WDFFILEOBJECT file)
{
    BX_MOUSE_REPORT released = {1,0,0,0,0,0};
    (void)Submit(BxContext(WdfFileObjectGetDevice(file)), &released);
}

VOID BxCleanup(WDFOBJECT device)
{
    BX_CONTEXT* context = BxContext(device);
    if (context->Vhf) { VhfDelete(context->Vhf, TRUE); context->Vhf = NULL; }
}
