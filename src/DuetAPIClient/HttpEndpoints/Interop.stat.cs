using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]

#pragma warning disable 8981

/*
 * Structures for the extended file attribute retrieval system call (statx()).
 *
 * See https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h
 */
internal struct statxbuf {
    internal uint Mask;         // What results were written [uncond]
    internal uint BlockSize;    // Preferred general I/O size [uncond] */
    internal ulong Attributes;  //  Flags conveying information about the file [uncond]
    internal uint HardLinks;    // Number of hard link
    internal uint UserID;       // User ID of owner
    internal uint GroupID;      // Group ID of owner
    internal ushort Mode;       // File mode
    private ushort Padding01;   // Spare space for future expansion
    internal ulong Inode;       // Inode number
    internal ulong Size;        // File size
    internal ulong Blocks;      // Number of 512-byte blocks allocated
    internal ulong AttributesMask; // Mask to show what's supported in stx_attributes
    internal statx_timestamp AccessTime;            // Last access time
    internal statx_timestamp CreationTime;          // File creation time
    internal statx_timestamp StatusChangeTime;      // Last attribute change time
    internal statx_timestamp LastModificationTime;  // Last data modification time
    internal uint RDevIdMajor;  // Device ID of special file [if bdev/cdev]
    internal uint RDevIdMinor;
    internal uint DevIdMajor;   // ID of device containing file [uncond]
    internal uint DevIdMinor;
    internal ulong MountId;
    internal uint DioMemAlign;  // Memory buffer alignment for direct I/O
    internal uint DioOffsetAlign; // File offset alignment for direct I/O
    // Spare space for future expansion
    private ulong Padding04;
    private ulong Padding05;
    private ulong Padding06;
    private ulong Padding07;
    private ulong Padding08;
    private ulong Padding09;
    private ulong Padding10;
    private ulong Padding11;
    private ulong Padding12;
    private ulong Padding13;
    private ulong Padding14;
    private ulong Padding15;
}

/*
 * Timestamp structure for the timestamps in struct statx.
 *
 * tv_sec holds the number of seconds before (negative) or after (positive)
 * 00:00:00 1st January 1970 UTC.
 *
 * tv_nsec holds a number of nanoseconds (0..999,999,999) after the tv_sec time.
 *
 * __reserved is held in case we need a yet finer resolution.
 */
internal struct statx_timestamp {
    public long tv_sec;
    public uint tv_nsec;
    public long __reserved;
}

internal static partial class Interop
{
    // Constants to be stx_dirfd
    // See https://github.com/torvalds/linux/blob/master/include/uapi/linux/fcntl.h
    internal const int AT_FDCWD             = -100;  // Special value used to indicate openat should use the current working directory.
    internal const int AT_SYMLINK_NOFOLLOW  = 0x100; // Do not follow symbolic links.
    internal const int AT_EACCESS           = 0x200; // Test access permitted for effective IDs, not real IDs.
    internal const int AT_REMOVEDIR         = 0x200; // Remove directory instead of unlinking file.
    internal const int AT_SYMLINK_FOLLOW    = 0x400; // Follow symbolic links
    internal const int AT_NO_AUTOMOUNT      = 0x800; // Suppress terminal automount traversal
    internal const int AT_EMPTY_PATH        = 0x1000; // Allow empty relative pathname
    internal const int AT_STATX_SYNC_TYPE   = 0x6000; // Type of synchronisation required from statx()
    internal const int AT_STATX_SYNC_AS_STAT = 0x0; // Do whatever stat() does
    internal const int AT_STATX_FORCE_SYNC  = 0x2000; // Force the attributes to be sync'd with the server
    internal const int AT_STATX_DONT_SYNC   = 0x4000; // Don't sync attributes with the server
    internal const int AT_RECURSIVE         = 0x8000; // Apply to the entire subtree

    /*
     * Flags to be stx_mask
     *
     * Query request/result mask for statx() and struct statx::stx_mask.
     *
     * These bits should be set in the mask argument of statx() to request
     * particular items when calling statx().
     *
     * See https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h
     */
    internal const int STATX_TYPE           = 0x0001; // Want/got stx_mode & S_IFMT  
    internal const int STATX_MODE           = 0x0002; // Want/got stx_mode & ~S_IFMT
    internal const int STATX_NLINK          = 0x0004; // Want/got stx_nlink
    internal const int STATX_UID            = 0x0008; // Want/got stx_uid
    internal const int STATX_GID            = 0x0010; // Want/got stx_gid
    internal const int STATX_ATIME          = 0x0020; // Want/got stx_atime
    internal const int STATX_MTIME          = 0x0040; // Want/got stx_mtime
    internal const int STATX_CTIME          = 0x0080; // Want/got stx_ctime
    internal const int STATX_INO            = 0x0100; // Want/got stx_ino
    internal const int STATX_SIZE           = 0x0200; // Want/got stx_size
    internal const int STATX_BLOCKS         = 0x0400; // Want/got stx_blocks
    internal const int STATX_BASIC_STATS    = 0x07ff; // The stuff in the normal stat struct
    internal const int STATX_BTIME          = 0x0800; // Want/got stx_btime
    internal const int STATX_MNT_ID         = 0x1000; // Got stx_mnt_id
    internal const int STATX_DIOALIGN       = 0x2000; // Want/got direct I/O alignment info
    /*
     * This is deprecated, and shall remain the same value in the future.  To avoid
     * confusion please use the equivalent (STATX_BASIC_STATS | STATX_BTIME)
     * instead.
     */
    internal const int STATX_ALL            = 0x0fff;

    /*
     * Attributes to be found in stx_attributes and masked in stx_attributes_mask.
     *
     * These give information about the features or the state of a file that might
     * be of use to ordinary userspace programs such as GUIs or ls rather than
     * specialised tools.
     *
     * Note that the flags marked [I] correspond to the FS_IOC_SETFLAGS flags
     * semantically.  Where possible, the numerical value is picked to correspond
     * also.  Note that the DAX attribute indicates that the file is in the CPU
     * direct access state.  It does not correspond to the per-inode flag that
     * some filesystems support.
     *
     * See https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h
     *
     */
    internal const int STATX_ATTR_COMPRESSED    = 0x0004; // [I] File is compressed by the fs
    internal const int STATX_ATTR_IMMUTABLE     = 0x0010; // [I] File is marked immutable
    internal const int STATX_ATTR_APPEND        = 0x0020; // [I] File is append-only
    internal const int STATX_ATTR_NODUMP        = 0x0040; // [I] File is not to be dumped
    internal const int STATX_ATTR_ENCRYPTED     = 0x0800; // [I] File requires key to decrypt in fs
    internal const int STATX_ATTR_AUTOMOUNT     = 0x1000; // Dir: Automount trigger
    internal const int STATX_ATTR_MOUNT_ROOT    = 0x2000; // Root of a mount
    internal const int STATX_ATTR_VERITY        = 0x100000; // [I] Verity protected file
    internal const int STATX_ATTR_DAX           = 0x200000; // File is currently in DAX state

    [DllImport(LibcLibrary, SetLastError = true, CharSet = CharSet.Ansi)]
    // See https://man7.org/linux/man-pages/man2/statx.2.html
    internal static extern int statx(int dirfd, string path, int flags, uint mask, ref statxbuf data);
}