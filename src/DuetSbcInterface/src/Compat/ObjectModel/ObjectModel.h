/*
 * ObjectModel.h - compatibility shim
 *
 * RepRapFirmware classes publish themselves into the firmware object model by inheriting a
 * reflection base and declaring a static descriptor table. Here the object model lives in
 * DuetControlServer, which builds it from what it already knows - it is the side that created every
 * move in the first place - so nothing on this side needs to be reflectable.
 *
 * These macros therefore expand to nothing, which lets an imported class keep its
 * `... final INHERIT_OBJECT_MODEL` declaration while its descriptor tables are deleted.
 */

#ifndef SRC_COMPAT_OBJECTMODEL_OBJECTMODEL_H_
#define SRC_COMPAT_OBJECTMODEL_OBJECTMODEL_H_

#include <RepRapFirmware.h>

#define INHERIT_OBJECT_MODEL
#define DECLARE_OBJECT_MODEL
#define DECLARE_OBJECT_MODEL_WITH_ARRAYS
#define DECLARE_OBJECT_MODEL_VIRTUAL

#endif /* SRC_COMPAT_OBJECTMODEL_OBJECTMODEL_H_ */
