/* vswhere.exe shim for msvc-wine under wine.
 *
 * Why: Unity's IL2CPP Bee toolchain locator
 * (Bee.Toolchain.VisualStudio.MsvcVersions.MsvcInstallationLocator) finds a
 * Visual Studio C++ toolchain by running vswhere.exe from the standard path
 * (Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe) and
 * parsing its XML output (ParseVSWhereResult(String xml)). msvc-wine provides a
 * complete VS layout (VC\Tools\MSVC\<ver>, VC\Auxiliary\Build\*, vcvars*.bat)
 * but is not "registered", so the real vswhere would report nothing.
 *
 * This shim reports the msvc-wine install (path from $INUTIL_MSVC_WIN) as a
 * Visual Studio 2022 instance. Mimics real vswhere: `-format xml` (default,
 * what Bee parses), `-format json`, and `-property <name>`.
 *
 * Pair this with the Windows 10 SDK registry key Bee reads separately:
 *   HKLM\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\v10.0
 *     InstallationFolder = <msvc>\Windows Kits\10\
 */
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

static void jpath(const char* p) { for (; *p; p++) { if (*p == '\\') putchar('\\'); putchar(*p); } }

int main(int argc, char** argv) {
    const char* root = getenv("INUTIL_MSVC_WIN");
    if (!root || !*root) root = "C:\\msvc";   /* scripts always set INUTIL_MSVC_WIN */
    const char* ver = "17.11.35219.272";

    int json = 0; const char* prop = NULL;
    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "-format") && i + 1 < argc && !strcmp(argv[i + 1], "json")) json = 1;
        if (!strcmp(argv[i], "-property") && i + 1 < argc) prop = argv[i + 1];
    }
    if (prop) {
        if (!strcmp(prop, "installationPath")) printf("%s\n", root);
        else if (!strcmp(prop, "installationVersion")) printf("%s\n", ver);
        else if (!strcmp(prop, "instanceId")) printf("msvcwine\n");
        else printf("\n");
        return 0;
    }
    if (json) {
        printf("[{\"instanceId\":\"msvcwine\",\"installationPath\":\""); jpath(root);
        printf("\",\"installationVersion\":\"%s\",\"displayName\":\"Visual Studio Community 2022\","
               "\"isComplete\":true,\"isLaunchable\":true,\"isPrerelease\":false,"
               "\"catalog\":{\"productLineVersion\":\"2022\"}}]\n", ver);
        return 0;
    }
    /* default / -format xml : the XML Unity's Bee locator parses */
    printf("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<instances>\n  <instance>\n");
    printf("    <instanceId>msvcwine</instanceId>\n");
    printf("    <installDate>2024-01-01T00:00:00Z</installDate>\n");
    printf("    <installationName>VisualStudio/17.11.0+35219.272</installationName>\n");
    printf("    <installationPath>%s</installationPath>\n", root);
    printf("    <installationVersion>%s</installationVersion>\n", ver);
    printf("    <productId>Microsoft.VisualStudio.Product.Community</productId>\n");
    printf("    <isComplete>1</isComplete>\n    <isLaunchable>1</isLaunchable>\n    <isPrerelease>0</isPrerelease>\n");
    printf("    <displayName>Visual Studio Community 2022</displayName>\n");
    printf("    <catalog>\n      <productLineVersion>2022</productLineVersion>\n");
    printf("      <productDisplayVersion>17.11.0</productDisplayVersion>\n    </catalog>\n");
    printf("  </instance>\n</instances>\n");
    return 0;
}
