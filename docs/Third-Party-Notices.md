# Third-party notices

OpenSim Studio's numerics are first-party. The data listed here is bundled from other authors
and keeps its own licence.

## Sobol direction numbers (Joe and Kuo)

- **What:** the table `new-joe-kuo-6.21201` (primitive polynomials and initial direction numbers
  for Sobol sequences in up to 21201 dimensions), bundled unchanged in
  `OpenSim.Core/Numerics/Data` and read by `OpenSim.Core.Numerics.SobolSequence`.
- **Source:** https://web.maths.unsw.edu.au/~fkuo/sobol/ (downloaded 2026-11-13; the authors
  last updated the table on 16 September 2010).
- **Reference:** S. Joe and F. Y. Kuo, "Constructing Sobol sequences with better two-dimensional
  projections", SIAM J. Sci. Comput. 30, 2635-2654 (2008).
- **Licence:**

```
-----------------------------------------------------------------------------
Licence pertaining to sobol.cc and the accompanying sets of direction numbers

-----------------------------------------------------------------------------
Copyright (c) 2008, Frances Y. Kuo and Stephen Joe
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright
      notice, this list of conditions and the following disclaimer.

    * Redistributions in binary form must reproduce the above copyright
      notice, this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.

    * Neither the names of the copyright holders nor the names of the
      University of New South Wales and the University of Waikato
      and its contributors may be used to endorse or promote products derived
      from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ``AS IS'' AND ANY
EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDERS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
