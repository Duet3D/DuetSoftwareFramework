/*
 * Float16Compat.h
 *
 * RRFLibraries' General/Portability.h defines float16_t as __fp16, which is an ARM extension: it
 * does not exist on x86-64, and the CI `native` preset builds there. The definition is already
 * guarded by FLOAT16_T_DEFINED, so claiming the name first is enough - no edit to RRFLibraries.
 *
 * This header is force-included (-include) into every translation unit of duet_motion and of the
 * HOST build of RRFLibraries, rather than being #included by hand. A plain include would only work
 * for translation units that reach Portability.h through a header we control, and the whole point
 * of the Compat layer is that the imported RepRapFirmware sources keep their original #includes.
 *
 * float is the right substitute rather than a 16-bit emulation: the only float16_t in the code kept
 * here was DDA::originalFeedRate, which exists for pause/resume reporting and is not sent to this
 * side of the split at all.
 */

#ifndef SRC_COMPAT_FLOAT16COMPAT_H_
#define SRC_COMPAT_FLOAT16COMPAT_H_

#ifndef FLOAT16_T_DEFINED
# define FLOAT16_T_DEFINED
using float16_t = float;
#endif

#endif /* SRC_COMPAT_FLOAT16COMPAT_H_ */
