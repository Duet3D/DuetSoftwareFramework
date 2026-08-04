/*
 * CoreTypes.h
 *
 * CANlib's headers include <CoreTypes.h>, which upstream comes from CoreN2G. CoreN2G is the Duet
 * hardware abstraction - clocks, pins, DMA, the CAN peripheral - and none of it means anything on
 * the SBC, so building it here to obtain one header of typedefs would be the wrong trade.
 *
 * This is that header, reduced to what CANlib's message definitions actually need. It sits under
 * Compat/CoreN2G/ so the directory it is found in matches the include line, exactly as the rest of
 * the Compat layer mirrors the upstream layout rather than editing the imported sources.
 *
 * The types are copied verbatim from lib/CoreN2G/src/CoreTypes.h and must stay that way: CanAddress
 * in particular is part of the wire format that the boards read. Upstream's NoPin and Nx constants
 * are left out: nothing in CANlib refers to them, and they are pin numbers, which is a concept this
 * side of the link does not have.
 */

#ifndef SRC_COMPAT_COREN2G_CORETYPES_H_
#define SRC_COMPAT_COREN2G_CORETYPES_H_

#include <cstdint>

using DmaChannel = uint8_t;   ///< A type that represents a DMA channel number
using DmaPriority = uint8_t;  ///< A type that represents a DMA priority
using Pin = uint8_t;          ///< A type that represents an I/O pin on the microcontroller
using PwmFrequency = uint16_t; ///< A type that represents a PWM frequency. 0 sometimes means "default".
using CanAddress = uint8_t;   ///< A type that represents the 7-bit CAN address of a board
using NvicPriority = uint32_t; ///< A type that represents an interrupt priority
using ExintNumber = uint8_t;  ///< A type that represents an EXINT number (used on SAME5x/SAMC21 only)
using EventNumber = uint8_t;  ///< A type that represents an event number (used on SAME5x/SAMC21 only)

// This one has to stay 16 bits wide. Unlike RRFLibraries, where float16_t appears only in a field
// that never crosses the link (see Compat/Float16Compat.h), CANlib puts it in the messages
// themselves - ShortMinCurMax and the pressure advance parameters - so widening it to float changes
// the size of a CAN message and trips CANlib's own "CAN message too big" assertion.
//
// The two compilers spell it differently and each rejects the other's spelling. __fp16 is the ARM
// one, which is what upstream uses and what the aarch64 cross compiler provides; _Float16 is the
// standard one, which is what x86-64 has.
//
// The choice is made on the target architecture rather than on __FLT16_MANT_DIG__, which looks like
// the right question but is not: the aarch64 compiler defines that macro while still rejecting
// _Float16, so asking it builds on the host and fails to cross compile - which is exactly how this
// got here.
#ifndef FLOAT16_T_DEFINED
# define FLOAT16_T_DEFINED
# if defined(__ARM_FP16_FORMAT_IEEE) || defined(__ARM_FP16_FORMAT_ALTERNATIVE)
using float16_t = __fp16;
# else
using float16_t = _Float16;
# endif
static_assert(sizeof(float16_t) == 2, "float16_t must be 16 bits wide or CAN messages change size");
#endif

#endif /* SRC_COMPAT_COREN2G_CORETYPES_H_ */
