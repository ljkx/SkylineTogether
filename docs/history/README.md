# Historical design records

The documents in this folder were written **before** Skyline Together was implemented. They are kept because they
explain where many ideas, names and trade-offs come from, and because some Core code that the game layer does not use
still follows them. They are **not authoritative** and are not maintained: much of what they describe was built
differently, simplified or never built, and the "As built" notes added to them later are incomplete.

For how the mod works today, read [docs/ARCHITECTURE.md](../ARCHITECTURE.md), which is written from the code.

| Document | What it was |
|---|---|
| [ARCHITECTURE-original.md](ARCHITECTURE-original.md) | The target architecture derived from the three proposals (section numbers `§x.y`, decisions `D1`-`D11` and `K1`-`K19`). Older code comments and commits cite it as "ARCH §x.y". |
| [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) | The original module plan, contracts and coding rules ("IMPLEMENTATION-PLAN §x"). |
| [proposal-A-max-fidelity.md](proposal-A-max-fidelity.md) | Competing proposal A: maximum fidelity. |
| [proposal-B-scale-efficiency.md](proposal-B-scale-efficiency.md) | Competing proposal B: scale and efficiency. |
| [proposal-C-buildable-robust.md](proposal-C-buildable-robust.md) | Competing proposal C: buildable and robust. |

Terms used in these documents: **R01**-**R15** are the [research reports](../research/); **P-A**, **P-B** and **P-C**
are the three proposals; **J1**, **J2** and **J3** are the three design reviews of the proposals (J1 judged player
experience, J2 engineering risk, J3 scale and performance); **ARCH** is the original architecture. Work-package tags
such as `w2/ids` or `w8/security` name development branches.

When a historical document and the code disagree, the code is right. Changes to the design are recorded in
[docs/ARCHITECTURE.md](../ARCHITECTURE.md) and, for decisions, in [docs/decisions/](../decisions/).
