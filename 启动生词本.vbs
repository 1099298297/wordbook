Option Explicit
Dim sh, fso, dir, exe, code
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = dir & "\desktop\bin\Release\net10.0-windows\Wordbook.exe"

If Not fso.FileExists(exe) Then
  code = sh.Run("cmd /c dotnet build """ & dir & "\desktop\Wordbook.csproj"" -c Release -v quiet", 0, True)
  If code <> 0 Then
    MsgBox "Auto build failed. Please install .NET 10 SDK and retry.", 48, "Wordbook"
    WScript.Quit 1
  End If
End If

sh.Run """" & exe & """ --open", 0, False
