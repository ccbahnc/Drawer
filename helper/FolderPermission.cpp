#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <string>
#include <vector>
#include <algorithm>
#include <cwctype>

static const std::wstring marker = L"A3C6E7D8-2B10-4F64-91DD-5F21D9A06701";
struct ComScope {
    HRESULT result=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    ~ComScope(){if(SUCCEEDED(result))CoUninitialize();}
};
static std::string ascii(const std::wstring& text) {
    std::string result;for(wchar_t ch:text){if(ch>127)return "";result.push_back(static_cast<char>(ch));}return result;
}
static std::wstring lower(std::wstring s) { std::transform(s.begin(), s.end(), s.begin(), towlower); return s; }
static bool under(const std::wstring& path, const std::wstring& parent) {
    auto p=lower(path),r=lower(parent); return p==r || p.rfind(r+L"\\",0)==0;
}
static std::wstring normalize(const std::wstring& input) {
    for(size_t i=3;i<=input.size();++i)
        if((i==input.size() || input[i]==L'\\' || input[i]==L'/') && (input[i-1]==L'.' || input[i-1]==L' '))return L"";
    wchar_t buffer[32768]; DWORD n=GetFullPathNameW(input.c_str(),32768,buffer,nullptr);
    if(!n || n>=32768)return L"";
    std::wstring result(buffer,n); while(result.size()>3 && result.back()==L'\\')result.pop_back();
    return result;
}
static bool safe(const std::wstring& path,const std::wstring& local) {
    if(local.size()<=3 || local[1]!=L':' || local[2]!=L'\\')return false;
    if(path.size()<=3 || path.size()>230 || path[1]!=L':' || path[2]!=L'\\' ||
       path.find_first_of(L"\"<>|*?")!=std::wstring::npos)return false;
    if(GetDriveTypeW(path.substr(0,3).c_str())!=DRIVE_FIXED)return false;
    if(lower(path)==lower(path.substr(0,3)+L"Program Files") || lower(path)==lower(path.substr(0,3)+L"Program Files (x86)"))return false;
    wchar_t windows[MAX_PATH]; if(!GetWindowsDirectoryW(windows,MAX_PATH) || under(path,normalize(windows)))return false;
    if(under(path,local+L"\\Drawer") || under(path,local+L"\\DrawerBackups") || under(local+L"\\Drawer",path))return false;
    for(auto id:{FOLDERID_ProgramData,FOLDERID_UserProfiles,FOLDERID_ProgramFilesCommon,FOLDERID_ProgramFilesCommonX86}) {
        PWSTR folder=nullptr; if(FAILED(SHGetKnownFolderPath(id,0,nullptr,&folder)) || !folder)return false;
        {
            bool forbidden=under(path,normalize(folder));CoTaskMemFree(folder);if(forbidden)return false;
        }
    }
    for(auto id:{FOLDERID_ProgramFiles,FOLDERID_ProgramFilesX86}) {
        PWSTR folder=nullptr; if(FAILED(SHGetKnownFolderPath(id,0,nullptr,&folder)) || !folder)return false;
        {
            bool forbidden=lower(path)==lower(normalize(folder));CoTaskMemFree(folder);if(forbidden)return false;
        }
    }
    auto current=path;
    while(current.size()>3) {
        auto pos=current.find_last_of(L'\\'); auto component=current.substr(pos+1);
        if(component.empty() || component.back()==L'.' || component.back()==L' ')return false;
        DWORD attr=GetFileAttributesW(current.c_str());
        if(attr!=INVALID_FILE_ATTRIBUTES && (!(attr&FILE_ATTRIBUTE_DIRECTORY) || (attr&FILE_ATTRIBUTE_REPARSE_POINT)))return false;
        if(attr==INVALID_FILE_ATTRIBUTES && GetLastError()!=ERROR_FILE_NOT_FOUND && GetLastError()!=ERROR_PATH_NOT_FOUND)return false;
        current=current.substr(0,pos);if(current.size()==2)current+=L'\\';
    }
    return true;
}
static std::string readSmall(const std::wstring& path) {
    HANDLE file=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
    if(file==INVALID_HANDLE_VALUE)return "";
    char buffer[512];DWORD n=0;bool ok=ReadFile(file,buffer,511,&n,nullptr)!=0;CloseHandle(file);
    if(!ok || n>=511)return "";return std::string(buffer,n);
}
static bool prepared(const std::wstring& target,const std::wstring& sid) {
    auto m=ascii(marker),s=ascii(sid);
    return readSmall(target+L"\\drawer-permission-id.txt")==m+"|"+s;
}
static bool emptyOrOwned(const std::wstring& target,const std::wstring& sid) {
    if(GetFileAttributesW(target.c_str())==INVALID_FILE_ATTRIBUTES)return true;
    auto m=ascii(marker);auto install=readSmall(target+L"\\drawer-install-id.txt");
    while(!install.empty() && (install.back()=='\r'||install.back()=='\n'))install.pop_back();
    if(install==m || prepared(target,sid))return true;
    WIN32_FIND_DATAW data{};HANDLE find=FindFirstFileW((target+L"\\*").c_str(),&data);
    if(find==INVALID_HANDLE_VALUE)return GetLastError()==ERROR_FILE_NOT_FOUND;
    bool empty=true;do {if(wcscmp(data.cFileName,L".") && wcscmp(data.cFileName,L"..")){empty=false;break;}}while(FindNextFileW(find,&data));
    FindClose(find);return empty;
}
static bool safeTree(const std::wstring& path,unsigned depth=0) {
    if(depth>32)return false;WIN32_FIND_DATAW data{};
    HANDLE find=FindFirstFileW((path+L"\\*").c_str(),&data);
    if(find==INVALID_HANDLE_VALUE)return GetLastError()==ERROR_FILE_NOT_FOUND || GetLastError()==ERROR_PATH_NOT_FOUND;
    bool ok=true;do {
        if(!wcscmp(data.cFileName,L".") || !wcscmp(data.cFileName,L".."))continue;
        if((data.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT) ||
          ((data.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY) && !safeTree(path+L"\\"+data.cFileName,depth+1))){ok=false;break;}
    }while(FindNextFileW(find,&data));FindClose(find);return ok;
}
int wmain(int argc,wchar_t** argv) {
    ComScope apartment;
    if(argc==4 && std::wstring(argv[1])==L"--validate-only")
        return safe(normalize(argv[2]),normalize(argv[3]))?0:ERROR_BAD_PATHNAME;
    if(argc!=5 || std::wstring(argv[1])!=L"--grant")return ERROR_INVALID_PARAMETER;
    auto target=normalize(argv[2]),local=normalize(argv[4]);
    if(!safe(target,local))return ERROR_BAD_PATHNAME;
    wchar_t* end=nullptr;unsigned long number=wcstoul(argv[3],&end,10);
    if(!number || !end || *end)return ERROR_INVALID_PARAMETER;
    HANDLE parent=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,number),token=nullptr;
    if(!parent)return GetLastError();
    if(!OpenProcessToken(parent,TOKEN_QUERY,&token)){DWORD error=GetLastError();CloseHandle(parent);return error;}
    CloseHandle(parent);DWORD needed=0;GetTokenInformation(token,TokenUser,nullptr,0,&needed);
    std::vector<BYTE> bytes(needed);
    if(!GetTokenInformation(token,TokenUser,bytes.data(),needed,&needed)){DWORD error=GetLastError();CloseHandle(token);return error;}
    CloseHandle(token);PSID sid=reinterpret_cast<TOKEN_USER*>(bytes.data())->User.Sid;
    LPWSTR sidText=nullptr;if(!ConvertSidToStringSidW(sid,&sidText))return GetLastError();
    std::wstring identity(sidText);LocalFree(sidText);
    SID_IDENTIFIER_AUTHORITY authority=SECURITY_NT_AUTHORITY;PSID administrators=nullptr;BOOL elevated=FALSE;
    AllocateAndInitializeSid(&authority,2,SECURITY_BUILTIN_DOMAIN_RID,DOMAIN_ALIAS_RID_ADMINS,0,0,0,0,0,0,&administrators);
    if(administrators){CheckTokenMembership(nullptr,administrators,&elevated);FreeSid(administrators);}
    if(!elevated)return ERROR_ELEVATION_REQUIRED;
    if(!safeTree(target))return ERROR_BAD_PATHNAME;
    if(!emptyOrOwned(target,identity))return ERROR_DIR_NOT_EMPTY;
    std::vector<std::wstring> missing;auto current=target;
    while(GetFileAttributesW(current.c_str())==INVALID_FILE_ATTRIBUTES) {
        missing.push_back(current);auto pos=current.find_last_of(L'\\');current=current.substr(0,pos);if(current.size()==2)current+=L'\\';
    }
    std::reverse(missing.begin(),missing.end());
    for(auto& folder:missing)if(!CreateDirectoryW(folder.c_str(),nullptr) && GetLastError()!=ERROR_ALREADY_EXISTS)return GetLastError();
    if(!safe(target,local) || !emptyOrOwned(target,identity))return ERROR_BAD_PATHNAME;
    // Work on a non-reparse directory handle rather than a second path lookup.
    // Denying share-delete also keeps the selected folder stable during the ACL update.
    HANDLE folder=CreateFileW(target.c_str(),READ_CONTROL|WRITE_DAC|FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
    if(folder==INVALID_HANDLE_VALUE)return GetLastError();
    BY_HANDLE_FILE_INFORMATION information{};
    if(!GetFileInformationByHandle(folder,&information) || (information.dwFileAttributes&FILE_ATTRIBUTE_REPARSE_POINT)) {
        CloseHandle(folder);return ERROR_BAD_PATHNAME;
    }
    wchar_t finalPath[32768];DWORD finalLength=GetFinalPathNameByHandleW(folder,finalPath,32768,0);
    if(!finalLength || finalLength>=32768){CloseHandle(folder);return ERROR_BAD_PATHNAME;}
    std::wstring resolved(finalPath,finalLength);
    if(resolved.rfind(L"\\\\?\\",0)==0)resolved.erase(0,4);
    if(!(information.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY) || lower(normalize(resolved))!=lower(target) || !safe(resolved,local)) {
        CloseHandle(folder);return ERROR_BAD_PATHNAME;
    }
    PACL oldAcl=nullptr,newAcl=nullptr;PSECURITY_DESCRIPTOR security=nullptr;
    DWORD error=GetSecurityInfo(folder,SE_FILE_OBJECT,DACL_SECURITY_INFORMATION,nullptr,nullptr,&oldAcl,nullptr,&security);
    if(error){CloseHandle(folder);return error;}
    if(!oldAcl){if(security)LocalFree(security);CloseHandle(folder);return ERROR_SUCCESS;}
    EXPLICIT_ACCESSW access{};access.grfAccessPermissions=FILE_GENERIC_READ|FILE_GENERIC_WRITE|FILE_GENERIC_EXECUTE|DELETE;
    access.grfAccessMode=GRANT_ACCESS;access.grfInheritance=SUB_CONTAINERS_AND_OBJECTS_INHERIT;
    access.Trustee.TrusteeForm=TRUSTEE_IS_SID;access.Trustee.TrusteeType=TRUSTEE_IS_USER;access.Trustee.ptstrName=static_cast<LPWSTR>(sid);
    error=SetEntriesInAclW(1,&access,oldAcl,&newAcl);
    if(!error)error=SetSecurityInfo(folder,SE_FILE_OBJECT,DACL_SECURITY_INFORMATION,nullptr,nullptr,newAcl,nullptr);
    if(newAcl)LocalFree(newAcl);if(security)LocalFree(security);CloseHandle(folder);
    // Any retry marker is written later by the unelevated installer, not here.
    return error;
}
