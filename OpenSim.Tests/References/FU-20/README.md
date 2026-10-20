# FU-20 — The Gerber macro-variable rule (PCB-19)

Reference material behind the variable tests in `OpenSim.Tests/Pcb/ApertureMacroTests.cs`
(`MacroExpressionTests.UndefinedVariable_*`, `SpecExample_Donutcal_*`, `RedefinedVariable_Throws`,
`DivisionByZero_ThrowsWithContext`, `DollarZero_IsNotAVariable`, `MacroGeometryTests.ShortAdCommand_*`).

## Source (downloaded 2026-10-19, with the owner's approval)

- Ucamco, *The Gerber Layer Format Specification*, Revision 2026.05 (the newest on the downloads page;
  219 pages). https://www.ucamco.com/files/downloads/file_en/554/gerber-layer-format-specification-revision-2026-05_en.pdf
  from https://www.ucamco.com/en/gerber/downloads
  (4,070,415 bytes, SHA-256 AC9E9899F573D69E472B2112F1AE879C70F6CD13E17DB41A1C885CAF69961662).
  The PDF is Ucamco's copyright and is not kept in the repository; it sits in the gitignored
  `reports/sources/FU-20-gerber/`.

## What the specification says

Section 4.5.4.2 "Arithmetic Expressions and New variables" (printed pages 72–74; the rules are on
p. 74). The four rules for macro variable values, in order:

1. $1, $2, …, $n take the values of the n parameters of the calling AD command.
2. A new variable gets its value from its defining expression (an assign statement, `$4=$1x1.25-$3`;
   the right-hand side is any arithmetic expression). The AM syntax table (4.5, p. 59) adds that the
   expression may use constants and variables defined previously, and the TEST2 example on p. 74
   interleaves assignments with primitives — so statements are evaluated in order, and a forward
   reference is an undefined variable (rule 3) at the point of use.
3. "Undefined variables are 0."
4. Macro variables cannot be redefined; an attempt to redefine one makes the file invalid. The AD
   parameters are macro variables (4.5.4.1, p. 72), so this covers `$1=…` as well.

A variable name is `$` followed by an integer > 0 (4.5, p. 59; grammar p. 39), so `$0` is not a
variable. Rule 3 is what the audit
(`reports/electrical-review-2026-10/audit-pcb.md`, PCB-19) recalled; the audit cited it as 4.5.4,
which in this revision is the parent section (4.5.4 "Macro Variables and Expressions").

Worked examples in the same section and in 4.5.5, used as test values:

- 4.5.4.2, DONUTCAL: body `1,1,$1,$2,$3* $5=$4x0.25* 1,0,$5,$2,$3*`, call
  `%ADD35DONUTCAL,0.020X0X0X0.06*%`. The spec gives $1 = 0.02, $2 = 0, $3 = 0, $4 = 0.06 and
  $5 = 0.06 × 0.25 = 0.015.
- 4.5.5.2 "Variable Parameter Values" (pp. 76–77; the same call also opens 4.5.4.1 on p. 72),
  DONUTVAR: body `1,1,$1,$2,$3* 1,0,$4,$2,$3*`, call `%ADD34DONUTVAR,0.100X0X0X0.080*%`, giving
  $1 = 0.100, $2 = 0, $3 = 0, $4 = 0.080.
  The test `ShortAdCommand_*` calls this body with one parameter so that $2, $3 and $4 are undefined.

## What changed in OpenSim

`OpenSim.Pcb/Gerber/MacroExpressionEvaluator.cs` threw an `InvalidDataException` ("Aperture macro
'<name>', statement '<statement>*': undefined macro variable $n.") and accepted a redefinition
silently. Now:

- an undefined $n evaluates to 0 (rule 3) and the import carries one warning per undefined variable
  per aperture evaluation, naming the macro, the statement, the variable and how many parameters
  the AD command supplied — the file is valid, so it is not an error, but a short %AD is still shown;
- redefining a variable, including one of the AD parameters, throws (rule 4);
- a division by zero still throws (the spec gives it no value);
- `$0` throws as a malformed name, in an expression and in an assignment (before, an expression's
  `$0` threw as undefined and `$0=` defined a variable 0).

The previous test `UndefinedVariable_AndDivisionByZero_ThrowWithContext` is split: its undefined half
is replaced by the tests above, its division half is `DivisionByZero_ThrowsWithContext`.

Not changed, and not in the spec: the evaluator also accepts an upper-case `X` as multiply. The spec's
operator table (p. 72) reads "x (lower case)" and files are case-sensitive (p. 32); the leniency
predates this item and is kept for emitters that use it.

## Left

- No real-world file with a short %AD or a redefinition is on this machine (the fixture board,
  `example_board.zip`, is absent — D22/FU-21); the rule is pinned by the spec's own examples only.
