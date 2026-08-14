/* Drop-in replacement for Unity.ILPP.Trigger.exe under wine.
 *
 * Why: Unity's editor checks IL-Post-Processor connectivity by running
 * Unity.ILPP.Trigger.exe, whose WaitForFileExists() calls .NET File.Exists()
 * on the runner's named pipe (\\.\pipe\unity-ilpp-<guid>). Under wine,
 * GetFileAttributes/FindFirstFile on the \\.\pipe\ namespace returns
 * ERROR_BAD_DEV_TYPE, so File.Exists() is false even though the pipe is fully
 * connectable (CreateFile/WaitNamedPipe work). The real Trigger therefore
 * throws "Can't find file", the editor reports "cannot establish connectivity",
 * and the build hangs forever.
 *
 * This shim performs the existence check with the APIs that DO work under wine
 * (WaitNamedPipe + CreateFile), confirms the runner pipe is connectable, then
 * exits 0 — which is all the editor needs to proceed. The editor's own IL2CPP
 * traffic to the runner goes over the same pipe via CreateFile and works fine.
 *
 * args: <pipename> <verb>   (e.g. unity-ilpp-<guid> ping)
 */
#include <windows.h>
#include <stdio.h>
#include <string.h>

int main(int argc, char** argv) {
    if (argc < 2) { printf("trigger_shim: no pipe name\n"); return 0; }
    char path[300];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\%s", argv[1]);
    HANDLE h = INVALID_HANDLE_VALUE;
    for (int i = 0; i < 120; i++) {            /* wait up to ~60s for the runner pipe */
        if (WaitNamedPipeA(path, 500)) {
            h = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
            if (h != INVALID_HANDLE_VALUE) break;
        }
        Sleep(500);
    }
    if (h == INVALID_HANDLE_VALUE) {
        fprintf(stderr, "trigger_shim: pipe %s never became connectable err=%lu\n", path, GetLastError());
        return 1;
    }
    fprintf(stderr, "trigger_shim: connected to %s (verb=%s)\n", path, argc > 2 ? argv[2] : "-");
    CloseHandle(h);
    return 0;
}
