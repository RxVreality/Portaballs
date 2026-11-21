The files in `files/` are copies of s&box engine source files from
https://github.com/Facepunch/sbox-public (commit a8a63b3ad2ad5dcc0712eac863810b70d94a26a0), changed for
Portaballs. The `.patch` files show exactly what was changed. They are under s&box's licence:

```
Copyright (c) 2025 Facepunch Studios Ltd

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Only C# source is changed. The native engine binaries that s&box's `Setup.bat` downloads are not
covered by that licence, are not part of this repository, and come to you from Facepunch under
their terms (https://store.steampowered.com/eula/590830_eula_0).
