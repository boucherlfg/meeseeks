@echo off
rem Completes the csharp 'purpose' update once Meeseeks has been closed.
copy /Y "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Meeseeks.dll.new" "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Meeseeks.dll"
del "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Meeseeks.dll.new"
del "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Meeseeks.exe.old" 2>nul
del "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Microsoft.CodeAnalysis.CSharp.dll.old" 2>nul
del "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Microsoft.CodeAnalysis.CSharp.Scripting.dll.old" 2>nul
del "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Microsoft.CodeAnalysis.dll.old" 2>nul
del "C:\Users\regli\.local\bin\meeseeks\src\Meeseeks\bin\Release\net10.0\Microsoft.CodeAnalysis.Scripting.dll.old" 2>nul
echo Update complete.
