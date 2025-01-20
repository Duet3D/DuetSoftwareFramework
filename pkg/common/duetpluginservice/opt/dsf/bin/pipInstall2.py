#!/usr/bin/env python

"""
Install python modules and create a virtual environment for the plugin.

# Copyright (C) 2023 Stuart Strolin all rights reserved.
# Released under The MIT License. Full text available via https://opensource.org/licenses/MIT

Useage:
Usage pipInstall.py -m <manifest file> -p <plugin path>

Both <manifest file. and <plugin path> are fully qualified

A virtual environment will be created for python at
<plugin Path>/VENV_FOLDER   (see constant section)

If version number is supplied it must use one of the following comparisons:
==
>=
<=
>
<
~=    Note ~= will be converted to >=

Version numbers specifying max / min ranges are not supported

Rules (executed in order):
Try to install unless:
        1. If the module is a Built-In or installed (and has no version number) ==> do nothing

Note: If a module is already installed but failes reinstall / update
      The occurence is logged and the install request is considered successful        

Exit Codes:

See class ExitCodes below.

Verson:
1.0.0
Initial Release
Version 1.1.0
Modified by Andy at Duet3d
Version 1.1.1
Fixed issue handling modules with no version number e.e shlex
"""


import subprocess
import sys
import logging
from pkg_resources import parse_version as version
import re
import os
import sysconfig
import argparse
import json

from enum import Enum, auto
from typing import Optional

# CONSTANTS
VENV_FOLDER = 'venv'
MANIFEST_KEY = 'sbcPythonDependencies'
PIP = 'pip'

if os.name == 'nt':  # Windows
    BIN_DIR = 'Scripts'
    PYTHON_VERSION = 'python.exe'
else:
    BIN_DIR = 'bin'
    PYTHON_VERSION = 'python'


class ExitCodes(Enum):
    NO_MANIFEST_PROVIDED = 1
    MANIFEST_DOES_NOT_EXIST = 2
    NO_PLUGIN_PROVIDED = 3
    PLUGIN_DOES_NOT_EXIST = 4
    UNSUPPORTED_CONDITIONAL = 5
    PROBLEM_CREATING_VENV = 6
    MANIFEST_ERROR = 7
    PIP_LIST_ERROR = 8
    UNEXPECTED_ERROR = 9
    FAILED_TO_INSTALL_MODULE = 10
    INVALID_DEPENDENCY = 11


class Dependency:
    regex = r'(^([\w\-_]+)((==|~=|>=|<=|>|<)((\d+!)?(\d)+(\.\d+)*(-?(a|b|rc)\d+)?(\.post\d+)?(\.dev\d+)?))?$)|(^git\+.*$)'

    class RegexGroups(Enum):
        PYPI = 0
        PACKAGE_NAME = 1
        VERSION_WITH_COMPARATOR = 2
        COMPARATOR = 3
        VERSION = 4
        VERSION_EPOCH = 5
        VERSION_MAJOR = 6
        VERSION_MINOR = 7
        VERSION_PRE = 8
        VERSION_PRE_TYPE = 9
        VERSION_POST = 10
        VERSION_DEV = 11
        GIT = 12

    class DepTypes(Enum):
        PYPI = auto()
        GIT = auto()
        NONE = auto()

    def __init__(self) -> None:
        self.package: str = None
        self.version: str = None
        self.comparator: str = None
        self.type = self.DepTypes.NONE

    @property
    def uri(self):
        package = "" if self.package is None else self.package
        comparator = "" if self.comparator is None else self.comparator
        version = "" if self.version is None else self.version
        return package + comparator + version

    @classmethod
    def parse(cls, text: str) -> Optional['Dependency']:
        """Convert text from plugin.json sbcPythonDependencies into object

        Args:
            text (str): line from sbcPythonDependencies
        """
        result = re.findall(cls.regex, text)

        if len(result) == 0:
            logger.error(f"Could not find package name in \"{text}\"")
            return None

        result = list(result[0])  # Convert tuple to list

        dep = Dependency()

        if result[cls.RegexGroups.PYPI.value] != '':
            # Standard PyPi dependency
            if result[cls.RegexGroups.COMPARATOR.value] == '~=':
                result[cls.RegexGroups.COMPARATOR.value] = '>='

            dep.type = cls.DepTypes.PYPI

            # Convert to canonical name format
            name = str(result[cls.RegexGroups.PACKAGE_NAME.value])
            name = name.lower()
            name = name.replace('-', '_')
            name = name.replace('.', '_')
            dep.package = name

            dep.comparator = str(result[cls.RegexGroups.COMPARATOR.value])
            dep.version = str(result[cls.RegexGroups.VERSION.value])

        elif result[cls.RegexGroups.GIT.value] != '':
            # Git dependency
            dep.type = cls.DepTypes.GIT
            dep.package = str(result[cls.RegexGroups.GIT.value])
            dep.comparator = None
            dep.version = None
        else:
            # Should be impossible to get here
            logger.error(f"Could not find package name in \"{text}\"")
            return None

        return dep


def createLogger(logname):  # Create a custom logger so messages go to journalctl#####
    global logger
    logger = logging.getLogger(logname)
    logger.propagate = False
    # Create handler for console output
    c_handler = logging.StreamHandler()
    c_format = logging.Formatter('%(message)s')
    c_handler.setFormatter(c_format)
    logger.addHandler(c_handler)
    logger.setLevel(logging.DEBUG)


def validateParams():
    parser = argparse.ArgumentParser(
        description='pipInstall2',
        allow_abbrev=False)
    # Environment
    parser.add_argument('-m', type=str, nargs=1, default=[],
                        help='module path')
    parser.add_argument('-p', type=str, nargs=1, default=[], help='plugin path')

    args = vars(parser.parse_args())  # Save as a dict

    mFile = args['m'][0]
    pPath = args['p'][0]

    if mFile is None:
        logger.info('Exiting: No manifest file (-m) was provided')
        sys.exit(ExitCodes.NO_MANIFEST_PROVIDED)
    elif not os.path.isfile(mFile):
        logger.info('Exiting: Manifest file ' + mFile + ' does not exist')
        sys.exit(ExitCodes.MANIFEST_DOES_NOT_EXIST)

    if pPath is None:
        logger.info('Exiting: No plugin path (-p) was provided')
        sys.exit(ExitCodes.NO_PLUGIN_PROVIDED)
    elif not os.path.isdir(pPath):
        logger.info('Exiting: Plugin file ' + mFile + ' does not exist')
        sys.exit(ExitCodes.PLUGIN_DOES_NOT_EXIST)

    return mFile, pPath


def parseVersion(request) -> Dependency:
    # Get the module name any conditional and version
    if ',' in request:
        logger.info('Unsupported Conditional in: ' + str(request))
        sys.exit(ExitCodes.UNSUPPORTED_CONDITIONAL)

    dep = Dependency.parse(request)

    if dep is None:
        sys.exit(ExitCodes.INVALID_DEPENDENCY)

    return dep


def runsubprocess(cmd):
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, check=True, shell=True)
        if result.returncode != 0:
            if str(result.stderr) != '' and '[Error]' in str(result.stderr):
                logger.info('Command Failure: ' + str(cmd))
                logger.debug('Error = ' + str(result.stderr))
                logger.debug('Output = ' + str(result.stdout))
                return False
        return result.stdout
    except subprocess.CalledProcessError as e1:
        pass
        # logger.info('ProcessError -- ' + str(e1))  #  Supress this error
    except OSError as e2:
        logger.info('OSError -- ' + str(e2))
    return False


def createPythonEnv(envPath):
    pythonFile = os.path.normpath(os.path.join(envPath, BIN_DIR, PYTHON_VERSION))
    if os.path.isfile(pythonFile):  # No need to recreate
        return

    cmd = PYTHON_VERSION + ' -m venv --system-site-packages  ' + envPath
    logger.info('Creating Python Virtual Environment at: ' + envPath)
    result = runsubprocess(cmd)
    if result != '':
        logger.info('Problem creating Virtual Environment')
        logger.info(result)
        sys.exit(ExitCodes.PROBLEM_CREATING_VENV)
    return


def getModuleList(mFile):
    with open(mFile) as jsonfile:
        try:
            config = json.load(jsonfile)
        except ValueError as e:
            logger.info(mFile + ' is not a properly formatted json file')
            logger.info(str(e))
            sys.exit(ExitCodes.MANIFEST_ERROR)

    mList = config[MANIFEST_KEY]

    return mList


def getInstalledVersion(m, envPath):
    if (m in sys.builtin_module_names): # Returns compiled modules will miss some std modules
        return 'Built-In'

    try:
        globals()[m] = __import__(m)  # Will likely not work if alternate python versions allowed in future
        result = globals()[m].__version__
        return result
    except (AttributeError): # No version information
        if globals()[m].__name__ == m: # Module is installed so treat it as a builtin
            return 'Built-In'
    except (ImportError, ModuleNotFoundError):  # Check to see if pip thinks its installed
        pythonFile = os.path.normpath(os.path.join(envPath, BIN_DIR, PYTHON_VERSION))
        cmd = pythonFile + ' -m ' + PIP + ' list'
        request = runsubprocess(cmd)
        if request is False:
            logger.info('Aborting: Failed to get pip list')
            sys.exit(ExitCodes.PIP_LIST_ERROR)
        # Normalise to lower case and underscore
        request = request.lower()
        request = request.replace('-', '_')

        if m in request:  # The module exists
            # Try to get version number
            regex = '^' + m + '\s+(.*)'
            result = re.findall(regex, request, flags=re.MULTILINE)
            if result and result[0] != '':  # version number found
                return result[0]
            else:
                logger.info('Module ' + m + ' exists but does not have a version number.')
                return('0') # Set version number to 0
        else:
            logger.info('Module ' + m + ' is not installed')
            return('None')


def installModule(dep: Dependency, envPath: str):
    pythonFile = os.path.normpath(os.path.join(envPath, BIN_DIR, PYTHON_VERSION))
    cmd = pythonFile + ' -m ' + PIP + ' install --no-cache-dir --upgrade ' + '"' + dep.uri + '"'
    result = runsubprocess(cmd)
    
    if result == False:  # module could not be installed
        return False

    return True


def installModules(mList, envPath):
    sList = []  # Success
    fList = []  # Fail
    for requestedVersion in mList:
        # Get the elements of the requested module - parseVersion may modify
        dep = parseVersion(requestedVersion)

        logger.info('Checking for python module: ' + dep.uri)

        # Check to see what is installed
        installedVersion = getInstalledVersion(dep.package, envPath)

        #  Determine next action
        if installedVersion == 'Built-In':  # Rule 1
            resultCode = 2
        else:
            installOk = installModule(dep, envPath)
            if installOk:
                resultCode = 1
            elif installedVersion not in ['Built_in', 'None']:
                resultCode = 4
            else:
                resultCode = 3

        # Exit the program with appropriate log entries
        if resultCode == 0:
            sList.append('Module "' + dep.uri + '" is already installed')
        elif resultCode == 1:
            sList.append('Module "' + dep.package + '" was installed or updated to version ' +
                         getInstalledVersion(dep.package, envPath))
        elif resultCode == 2:
            sList.append('Module "' + dep.package + '" is a built-in.')
        elif resultCode == 3:
            fList.append('Module "' + dep.uri + '"')
        elif resultCode == 4:
            sList.append('Module "' + dep.package + '" was not updated from version ' + installedVersion)
        else:
            logger.info('An unexpected error occured')
            sys.exit(ExitCodes.UNEXPECTED_ERROR)

    return sList, fList


def main(progName):
    #  Set up logging so journalc can be used
    createLogger(progName)
    logger.info('---------------------------------------------------')
    logger.info(os.path.basename(sys.argv[0]) + ' is attempting to install python modules')

    #  Validate that the call was well formed and get the arguments
    manifestFile, pluginPath = validateParams()
    venvPath = os.path.normpath(os.path.join(pluginPath, VENV_FOLDER))

    #  Create virtual environment
    createPythonEnv(venvPath)

    # parse the manifestfile
    moduleList = getModuleList(manifestFile)

    # Install the modules
    successList = []
    failList = []

    successList, failList = installModules(moduleList, venvPath)

    if len(successList) > 0:
        logger.info('-----------------------------------------------')
        logger.info('The following modules were installed or updated:')
        for entry in successList:
            logger.info(entry)

    if len(failList) > 0:
        logger.info('!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!')
        logger.info('The following modules could not be installed:')
        for entry in failList:
            logger.info(entry)
        logger.info('!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!')
        sys.exit(ExitCodes.FAILED_TO_INSTALL_MODULE)

    logger.info('---------------------------------------')
    logger.info('All modules were successfully installed')
    logger.info('---------------------------------------')


if __name__ == "__main__":  # Do not run anything below if the file is imported by another program
    programName = sys.argv[0]
    main(programName)
