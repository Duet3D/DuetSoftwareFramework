# Overview

**This page is auto-generated. If you find any errors, please report them on the [forum](https://forum.duet3d.com) and DO NOT EDIT this page!**
If you are capable of editing source code, it is encouraged to make pull requests for the original [DSF API code](https://github.com/Duet3D/DuetSoftwareFramework/tree/v3.3-dev/src/DuetAPI/ObjectModel) instead.

This page refers to version 3.3 of the Object Model.

All Duet software projects share the same object model to store configuration and sensor data.
This page provides documentation about the different object model keys and associated properties.

Some fields may not be available in standalone mode because some fields are only maintained by DSF and/or DWC.
It is advised to consider this when developing applications that address Duets in standalone *and* SBC mode.

Certain fields have class names in braces `(...)` appended to the object model path.
These class names are present for items where different item types may be configured.
If a class inherits values from a base type, the inheritance is marked using a colon (`:`) followed by the base class name.
So, for example, `LaserFilamentMonitor : FilamentMonitor` means that a `LaserFilamentMonitor` inherits all the properties from the `FilamentMonitor` base class.

In standalone mode, each main key (like `boards` or `heat`) has its own sequence number in the `seqs` key which is not documented here.
Whenever a non-live field is updated (see `M409 F"f"`), this sequence number is increased.
For clients targeting standalone mode, it can be helpful to check these values to determine when it is time to request a full key from RRF again.
There is an extra value `seqs.reply` as well which is used notify clients about new messages (see `rr_reply`).
Note that these sequence numbers are not exposed in SBC mode.

*This page is preliminary and subject to further changes.*

# Object Model Description

