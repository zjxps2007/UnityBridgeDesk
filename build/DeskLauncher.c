#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <wchar.h>
#include <stdlib.h>

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    (void)instance; (void)previous; (void)show;
    WCHAR directory[32768], executable[32768];
    DWORD length = GetModuleFileNameW(NULL, directory, 32768);
    if (!length || length >= 32768) goto failed;
    WCHAR *separator = wcsrchr(directory, L'\\');
    if (!separator) goto failed;
    *separator = L'\0';
    if (wcslen(directory) + wcslen(L"\\app\\UnityBridgeDesk.exe") >= 32768) goto failed;
    wcscat(directory, L"\\app");
    wcscpy(executable, directory);
    wcscat(executable, L"\\UnityBridgeDesk.exe");
    DWORD attributes = GetFileAttributesW(executable);
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) goto failed;

    // Resolve from this executable so the complete folder can be moved to another PC.
    size_t capacity = wcslen(executable) + wcslen(arguments) + 4;
    if (capacity > 32767) goto failed;
    WCHAR *command = calloc(capacity, sizeof(WCHAR));
    if (!command) goto failed;
    swprintf(command, capacity, L"\"%ls\" %ls", executable, arguments);
    STARTUPINFOW startup = {0};
    PROCESS_INFORMATION process = {0};
    startup.cb = sizeof(startup);
    BOOL launched = CreateProcessW(executable, command, NULL, NULL, FALSE, 0, NULL, directory, &startup, &process);
    free(command);
    if (!launched) goto failed;
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return 0;

failed:
    MessageBoxW(NULL,
        L"UnityBridge Desk를 열지 못했습니다.\n\n"
        L"ZIP 파일의 압축을 모두 푼 뒤 다시 실행해 주세요.\n"
        L"이 실행 파일과 app 폴더는 같은 폴더에 있어야 합니다.",
        L"UnityBridge Desk · 실행 안내", MB_OK | MB_ICONINFORMATION);
    return 1;
}
