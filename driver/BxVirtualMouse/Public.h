#pragma once
#define IOCTL_BX_MOUSE_REPORT CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_WRITE_DATA)
#pragma pack(push, 1)
typedef struct _BX_MOUSE_REPORT {
    UCHAR ReportId;
    UCHAR Buttons;
    SHORT X;
    SHORT Y;
    CHAR Wheel;
    CHAR HorizontalWheel;
} BX_MOUSE_REPORT, *PBX_MOUSE_REPORT;
#pragma pack(pop)
