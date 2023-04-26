#!/usr/bin/python3
"""
Install python modules

# Copyright (C) 2023 Stuart Strolin all rights reserved.
# Released under The MIT License. Full text available via https://opensource.org/licenses/MIT

Useage:
Usage pip3_installer <module name>

Expects a single module with or without version number
If version number is supplied it must use one of the following forms
==
>=
<=
~=    Note ~= will be converted to >=

Version numbers specifying max / min ranges are not supported

Because this is a shared environment:
Rule 1:  If requested version is > current version, install requested version.
Rule 2:  If requested version < current version, do nothing

Return Codes:

0 - Successfully installed or already installed.  Details are sent to journalctl
1 = Something nasty happened
64 - No module was specified.
65 - Only one module can be specified. 
66 - Unsupported Conditional
67 - Pip3 could not handle the request

Verson 1.0.1
Initial Release

"""
import subprocess
import sys
import logging
from pkg_resources import parse_version as version
import re

def createLogger(logname):   ##### Create a custom logger so messages go to journalctl#####
    global logger
    logger = logging.getLogger(logname)
    logger.propagate = False
    # Create handler for console output
    c_handler = logging.StreamHandler()
    c_format = logging.Formatter('%(message)s')
    c_handler.setFormatter(c_format)
    logger.addHandler(c_handler)
    logger.setLevel(logging.DEBUG)


def parseArguments(request):      ##### Get the module name any conditional and version#####
    conditionals = '|'.join(['==', '~=', '>=', '<='])
    regex = '(.+)(' + conditionals + ')(.+)'
    result = re.findall(regex,request)
    if len(result) == 0: #  no conditional version
        if '>' in request or '<' in request:
            logger.info('Unsupported Conditional. ' + request)
            sys.exit(66)
        result = []
        result = [(request, '>=', '0')] # Any version

    result = list(result[0]) # Convert tuple to list
    if result[1] =='~=': # Rule 1
        result[1] = '>='

    return result[0], result[1], result[2]

def runsubprocess(cmd):
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, check=True, shell=True)
        if str(result.stderr) != '' and '[Error]' in str(result.stderr):
            logger.info('Command Failure: ' + str(cmd))
            logger.debug('Error = ' + str(result.stderr))
            logger.debug('Output = ' + str(result.stdout))
            return False
        else:
            return result.stdout
    except subprocess.CalledProcessError as e1:
        logger.info(str(e1))
    except OSError as e2:   
        logger.info(str(e2))
    return False

def pipInstalled(m): # If installed by pip and not a built-in - should return version number
    try:
        globals()[m] = __import__(m)  #  Raises ImportError is module is not available
        result = globals()[m].__version__
        return result
    except AttributeError: # Likely a built-in
        return ''

def checkInstall(mName,mCompare, mVersion): #Check to see if we need to install or not
    # return codes
    # 0 - already installed at same or higher version
    # 1 - installed, no version was requested
    # 2 - installed at higher version than current
    # 3 - could not install module

    try:
        installedVersion = pipInstalled(mName)  #  Raises ImportError is module is not available
        if installedVersion == '':              # A built-in so no need to proceed
            return 0 , installedVersion
        
        #  Compare reqested version (as modified) with installed version
        comparison = 'version("' + installedVersion + '")' + mCompare + 'version("' + mVersion + '")'  # version numbers are strings
        if exec(comparison): #Rule 1  Need to install later version
            logger.info('Attempting to install newer version ' + str(mVersion))
            raise ImportError  # try to install the higher version
        
        # Later version already installed
        return 0 , installedVersion
    except ImportError:  # try to install module
        cmd = 'pip3 install --no-cache-dir "' + mName+mCompare+mVersion + '"' # dont cache the download - saves some space
        result = runsubprocess(cmd)

        if result == False:  # module could not be installed
            return 3, mVersion  
        else:  #  Confirm thatthe new module can be imported        
            try:
                installedVersion = pipInstalled(mName)  # Raises ImportError if still cannot import
                if mVersion == '':
                    return 1, installedVersion  # Installed no version requested
                else:
                    return 2, installedVersion # Installed version requested
            except ImportError:
                return 3, mVersion  # module could not be installed

def main(progName):
    # Set up logging so jpurnalc can be used
    createLogger(progName)

    #  Validate that the call was well formed
    numArgs = len(sys.argv)
    if numArgs <= 1:
        logger.info('No module was specified: ' + str(sys.argv))
        sys.exit(64)
    elif numArgs > 2:
        logger.info('Only one module allowed.' + str(sys.argv))
        sys.exit(65)
    elif ',' in sys.argv[1]:
        logger.info('Unsupported Conditional. ' + str(sys.argv[1]))
        sys.exit(66)

    requestedVersion = sys.argv[1]  # Get the command line arguments
    
    # Get the elements of the request
    mName, mCompare,  mVersion = parseArguments(requestedVersion)
    
    # Check to see if installed and if not try to install
    returncode, installedVersion = checkInstall(mName, mCompare, mVersion)

    if installedVersion != '':
        installedVersion = ' ' + installedVersion  # So the log looks good
    
    # Exit the program with appropriate log entries
    if returncode == 0:
        logger.info('Module ' + mName +  installedVersion + ' is already installed')
        sys.exit(0)
    elif returncode == 1:
        logger.info('Module ' + mName  + installedVersion + ' was successfully installed.')
        sys.exit(0)
    elif returncode == 2:
        logger.info('Module ' + mName + ' installed. Requested: ' + mCompare + mVersion + ' Installed:' + installedVersion)
        sys.exit(0)
    elif returncode == 3:
        logger.info('Module ' + mName + mCompare + installedVersion + ' could not be installed.')
        logger.info('Check the module name and version number(if provided).')
        sys.exit(67)
    else:
        logger.info('An unexpected error occured')
        sys.exit(1)
    
if __name__ == "__main__":  # Do not run anything below if the file is imported by another program
    programName = sys.argv[0]
    main(programName)