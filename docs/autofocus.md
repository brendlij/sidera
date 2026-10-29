# Autofocus

> The search described here is tested against simulated stars only. It has not been run on a real sky yet.

An autofocus exposes at several focuser positions, measures the **HFR** (half flux radius) of the stars in each frame, fits the curve, moves to the best position and checks it. Use it from the Imaging page (the **Autofocus** panel), as an **Autofocus** action in a session, or as the automation of a block (at the start, every N minutes, after a filter change).

## The search

By default autofocus **searches**, in **two passes** (Settings → Autofocus → *Search for the focus*):

1. It starts **where the focuser stands** and measures there. When the stars there are much larger (twice) than where focus was found **last**, it measures at that position next (Sidera remembers it for each setup in `focus-history.json` beside the settings) and goes on from the better of the two.
2. **Coarse pass**, with the step size: it steps toward the side where the stars get smaller and **turns round** when they get larger, until it has as many samples on each side of the smallest stars as the number of samples says (for 7 samples, 3 on each side). Far out of focus the size of the stars hardly changes from one position to the next; there the steps get bigger.
3. A position where **no star can be measured** (blurred too much) ends the search on that side. It does not fail the run; only a run of failed samples does (3 by default).
4. The curve is fitted through the samples. A minimum **beyond** the samples is stepped to. It has to be a **clear minimum**: the stars at the two ends of the samples are clearly (30 %) larger than at the smallest, otherwise there is no curve and the run fails with that reason, instead of reporting a focus in the noise. A best HFR of more than three times the one of the last focus is not believed either.
5. **Fine pass**: a quarter of the step size around the position of the coarse pass, with fewer samples. When it finds no curve, the coarse result stands (*A second, finer pass* switches it off).
6. The focuser goes to the fitted position and one more exposure checks it. A focus that checks worse than the best sample **falls back to that sample** instead of failing.

The search never goes further than the **search range** from where it started (0 is ten steps of the step size) and never takes more than the **most samples** (30 or three times the samples, when it is 0).

With no star where the focuser stands, the search tries the position where focus was found last, then two and four steps to either side. If nothing can be measured it says so and stops.

## The fit

- **Hyperbola** (the default): the HFR of a star is a hyperbola in the focuser position, so its square is a parabola. Sidera fits that through the widest run of samples that the curve explains, so a wild outer sample (a satellite, a cloud) is left out instead of spoiling the fit.
- **Straight lines**: one line through the samples on each side of the minimum; the focus is where they cross. More robust when the curve is far from a hyperbola.

## Settings

**Settings → Autofocus**

| Setting | Meaning |
|---|---|
| Exposure, step size, samples | What an autofocus starts with (the samples are an odd number from 5 to 21) |
| Search for the focus | Off uses the older fixed pattern of samples around the current position |
| A second, finer pass | A quarter of the step around the result of the first pass |
| Search range | How far from where it started the search may go, in steps to either side; 0 is ten steps of the step size |
| Fit straight lines | Instead of a hyperbola |
| Go to the position where focus was found last | When no star is found where the focuser stands |
| Most samples | 0 lets the search decide (four times the samples and a few more) |
| Samples that may fail in a row | How many blurred samples are put up with |
| Hold the mount still while focusing | Other setups on the same mount wait |

The step size has to be large enough that the HFR changes clearly between samples; if it hardly changes, the run says to use a larger step.

## Not there yet

Positions by temperature or by filter, and compensating the backlash of the focuser.
