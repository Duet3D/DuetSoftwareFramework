using DuetAPI.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DuetPiManagementPlugin
{
    /// <summary>
    /// Functions for mounting and unmounting shares
    /// </summary>
    public static class Mount
    {
        /// <summary>
        /// Mount a device to a given directory
        /// </summary>
        /// <param name="endpoint">Endpoint to mount</param>
        /// <param name="directory">Directory to mount to</param>
        /// <param name="options">Mount options</param>
        /// <returns>Mount result</returns>
        public static async Task<Message> MountShare(string device, string? physicalDirectory, string? type, string? options)
        {
            if (device.Replace(@"\\", string.Empty).ToCharArray().Any(c => c != '.' && c != ':' && c != '/' && c != '-' && c != '_' && c != '#' && !char.IsLetterOrDigit(c)))
            {
                return new Message(MessageType.Error, "Invalid characters in the specified mount device");
            }
            if (!string.IsNullOrWhiteSpace(physicalDirectory) && !Directory.Exists(physicalDirectory))
            {
                return new Message(MessageType.Error, "Specified directory does not exist");
            }
            if (!string.IsNullOrWhiteSpace(type) && type.ToCharArray().Any(c => !char.IsLetterOrDigit(c)))
            {
                return new Message(MessageType.Error, "Invalid characters in the specified mount type");
            }
            if (!string.IsNullOrWhiteSpace(options) && options.Replace(@"\\", string.Empty).ToCharArray().Any(c => c != '.' && c != ':' && c != '/' && c != '-' && c != '_' && c != ',' && c != '=' && !char.IsLetterOrDigit(c)))
            {
                return new Message(MessageType.Error, "Invalid characters in the specified mount options");
            }

            string typeParam = string.IsNullOrWhiteSpace(type) ? string.Empty : $"-t {type} ";
            string optParam = string.IsNullOrWhiteSpace(options) ? string.Empty : $" -o {options}";
            string result = await Command.Execute("mount", $"{typeParam}{device} {physicalDirectory}{optParam}");
            return new Message(MessageType.Success, result);
        }

        /// <summary>
        /// Unmount a device or directory
        /// </summary>
        /// <param name="node">Node to unmount</param>
        /// <returns>Mount result</returns>
        public static async Task<Message> UnmountShare(string node)
        {
            if (node.Replace(@"\\", string.Empty).ToCharArray().Any(c => c != '.' && c != ':' && c != '/' && c != '-' && c != '_' && c != '#' && !char.IsLetterOrDigit(c)))
            {
                return new Message(MessageType.Error, "Invalid characters in the specified mount device");
            }

            string result = await Command.Execute("umount", node);
            return new Message(MessageType.Success, result);
        }
    }
}
