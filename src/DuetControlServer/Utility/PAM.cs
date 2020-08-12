using System.IO;
using System.Threading.Tasks;

namespace DuetControlServer.Utility
{
    /// <summary>
    /// Static class holding functions for PAM interaction
    /// </summary>
    public static class PAM
    {
        /// <summary>
        /// Get the UID from a user name
        /// </summary>
        /// <param name="username">Username to look up</param>
        /// <returns>UID or -1 if not found</returns>
        public static async Task<int> GetUserId(string username)
        {
            using FileStream fileStream = new FileStream("/etc/passwd", FileMode.Open, FileAccess.Read, FileShare.Read);
            using StreamReader reader = new StreamReader(fileStream);

            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string[] columns = line.Split(':');
                if (columns.Length >= 3)
                {
                    if (columns[0] == username)
                    {
                        return int.Parse(columns[2]);
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Get the username from a UID
        /// </summary>
        /// <param name="id">User ID</param>
        /// <returns>Username or null if not found</returns>
        public static async Task<string> GetUserName(int id)
        {
            using FileStream fileStream = new FileStream("/etc/passwd", FileMode.Open, FileAccess.Read, FileShare.Read);
            using StreamReader reader = new StreamReader(fileStream);

            string line, uid = id.ToString();
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string[] columns = line.Split(':');
                if (columns.Length >= 3)
                {
                    if (columns[2] == uid)
                    {
                        return columns[0];
                    }
                }
            }

            return null;
        }


        /// <summary>
        /// Get the group ID from a group name
        /// </summary>
        /// <param name="groupName">Group name</param>
        /// <returns>GID or -1 if not found</returns>
        public static async Task<int> GetGroupId(string groupName)
        {
            using FileStream fileStream = new FileStream("/etc/group", FileMode.Open, FileAccess.Read, FileShare.Read);
            using StreamReader reader = new StreamReader(fileStream);

            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string[] columns = line.Split(':');
                if (columns.Length >= 3)
                {
                    if (columns[0] == groupName)
                    {
                        return int.Parse(columns[2]);
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Get the group name from a GID
        /// </summary>
        /// <param name="id">Group ID</param>
        /// <returns>Group name or null if not found</returns>
        public static async Task<string> GetGroupName(int id)
        {
            using FileStream fileStream = new FileStream("/etc/group", FileMode.Open, FileAccess.Read, FileShare.Read);
            using StreamReader reader = new StreamReader(fileStream);

            string line, gid = id.ToString();
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string[] columns = line.Split(':');
                if (columns.Length >= 3)
                {
                    if (columns[2] == gid)
                    {
                        return columns[0];
                    }
                }
            }

            return null;
        }
    }
}
