
define HELP_TEXT

Duet Software Framework Makefile

$ make <variables> <targets>

Variables:
    ARCH=< armhf | armhfp | arm64 | aarch64 | x86_64 etc >
        Defaults to armhf
    CONFIG=< Debug | Release >
        Defaults to Debug
    DESTDIR=< path_to_output >
        Defaults to ./bin
    V=< 0 | 1 >
        Verbosity level, defaults to 0

Targets:
    Group:       Individual targets
*   build:      $(addsuffix .build,$(DIRS)) DuetWebControl.build
    publish:    $(DIRS_PUBLISH) DuetRuntime.publish DuetWebControl.publish
    buildroots: $(BUILDROOTS)
    debs:       $(DEBS)
    rpms:       $(RPMS)

    clean:      Cleans the dotnet projects.
                Cleans out the ./bin directory except for packages.
                Will not touch DESTDIR if specified
    distclean:  Cleans the dotnet projects.
                Removes the ./bin directory
                Will not touch DESTDIR if specified

    release:    Builds armhf and arm64 .deb packages
	            Builds armhfp and aarch64 .rpm packages

    release-deb:       Builds armhf and arm64 .deb packages
    release-armhf-deb: Builds armhf .deb packages
    release-arm64-deb: Builds arm64 .deb packages

    release-rpm:         Builds armhfp and aarch64 .rpm packages
    release-armhfp-rpm:  Builds armhfp .rpm packages
    release-aarch64-rpm: Builds aarch64 .rpm packages

Output:

    $$(DESTDIR)/$$(CONFIG)/$$(ARCH)/
       publish/$$(TARGET)/        Output from dotnet publish
       buildroot/$$(TARGET)/      Buildroots for packaging
    $$(DESTDIR)/packages/
       deb/                      Built deb packages
       rpm/                      Build rpm packages

Customization:

    If a file named Makefile.local is present in the top
    DSF directory, it will be included in the process.
    You can specify your own targets, different variable
    defaults and, most importantly, a KEY_ID which, if
    present, will be used to sign the packages.  This file
    is not tracked by git.

    If you specify targets in this file, the first one that
    doesn't begin with a "." will be the default target.

    Example:
        BUILD_ARCH = aarch64
        KEY_ID := $$(shell cat $$(HOME)/.ssh/dsf-signing-key)

        all:  release-deb

        test-install: buildroots
            (copy all the binaries to a test SBC)

endef


TOPDIR := $(shell pwd)

ifeq ($(V),1)
    ECHO_PREFIX=@
    CMD_PREFIX=
    SUPPRESS_OUTPUT=
else
    ECHO_PREFIX=@
    CMD_PREFIX=@
    SUPPRESS_OUTPUT= >/dev/null 2>&1
endif
MAKEOPTS := -s --no-print-directory
RSYNC := rsync -qaH

# These variables need to be dynamically expanded
# so we use the simple "=" assignment
BUILD_ARCH = $(if $(ARCH),$(ARCH),armhf)
CONFIG = Debug
DESTDIR = $(TOPDIR)/bin
CONFIGDIR = $(DESTDIR)/$(CONFIG)
BINDIR = $(CONFIGDIR)/$(BUILD_ARCH)

# These variables are static and only need to be expanded once
# so we use the ":=" assignment
DIRS := DuetControlServer DuetWebServer CodeConsole CodeLogger CustomHttpEndpoint ModelObserver PluginManager
# DIRS_BUILD will expaned to DuetControlServer-build DuetWebServer-build, etc.
# Same for the following rules.
DIRS_BUILD := $(addsuffix .build,$(DIRS))
DIRS_CLEAN := $(addsuffix .clean,$(DIRS))
DIRS_PUBLISH := $(addsuffix .publish,$(DIRS))
PACKAGES := DuetControlServer DuetWebServer DuetTools DuetRuntime DuetSD DuetSoftwareFramework DuetWebControl
BUILDROOTS := $(addsuffix .buildroot,$(PACKAGES))
RPMS := $(addsuffix .rpm,$(PACKAGES))
DEBS := $(addsuffix .deb,$(PACKAGES))

# Again, these are static variables. No need to run xmllint
# every time DuetControlServer-version is referenced.
# Once is enough.
DuetControlServer-version := $(shell xmllint --xpath "string(//Project/PropertyGroup/Version)" src/DuetControlServer/DuetControlServer.csproj)
DuetWebServer-version := $(shell xmllint --xpath "string(//Project/PropertyGroup/Version)" src/DuetWebServer/DuetWebServer.csproj)
DuetTools-version := $(DuetControlServer-version)
DuetRuntime-version := $(DuetControlServer-version)
DuetSoftwareFramework-version := $(DuetControlServer-version)
DuetSD-version := $(shell sed -n -r -e "s/^Version:\s+([0-9.]+)$$/\1/p" pkg/deb/duetsd/DEBIAN/control)
DuetWebControl-repo := https://github.com/chrishamm/DuetWebControl.git
# Exception...  This variable has to be dynamically expanded because
# at the time the Makefile is parsed, DWC may not have been
# downloaded or updated yet.
DuetWebControl-version = $(shell jq -r ".version" $(CONFIGDIR)/all/publish/DuetWebControl/package.json 2>/dev/null)

# When you have too much time on your hands :)
TARGET_PRINTF = printf " [%-9s] %-21s %-7s %-7s %-7s %s\n"
TARGET_TITLE = $(TARGET_PRINTF) $(subst .,,$(suffix $@)) \
	$(basename $@) "$(VERSION)" "$(CONFIG)" "$(BUILD_ARCH)" $(MSG)


ifeq ($(MAKECMDGOALS),help)
$(error $(HELP_TEXT))
endif

#
# Makefile.local (if present) gives you the opportunity to
# override any of the above variables or create your own
# targets.  For instance you may want to override the default
# ARCH to something else or create a target that just
# builds certain things.
#
# This is also the place to define a KEY_ID variable with
# the contents of your signing key.  If present, packages
# will be signed with it.
#
# Example:
#
# BUILD_ARCH = aarch64
# KEY_ID := $(shell cat $(HOME)/.ssh/dsf-signing-key)
#
-include Makefile.local

# Simple targets.

# build is the default unless you override it in your Makefile.local
build: $(DIRS_BUILD) DuetWebControl.build

publish: $(DIRS_PUBLISH) DuetRuntime.publish DuetWebControl.publish

buildroots: $(BUILDROOTS)

rpms: $(RPMS)

debs: $(DEBS)

clean: $(DIRS_CLEAN)
	$(ECHO_PREFIX)echo " [CLEAN    ] DuetSoftwareFramework"
	$(CMD_PREFIX)rm -rf bin/Debug bin/Release

distclean: clean
	$(CMD_PREFIX)rm -rf bin

# Both of these packages are architecture independent
# so we can set BUILD_ARCH once for all their steps
DuetSD.%: BUILD_ARCH = all
DuetWebControl.%: BUILD_ARCH = all

#
# The following rules more properly belong in
# the DuetWebControl project but it currently doesn't
# have its own Makefile.
#
# We need to force the git stuff to see if DWC has
# been updated.  Building it take a while so there's
# no need to do it if it hasn't changed.
#
FORCE:
$(CONFIGDIR)/all/publish/DuetWebControl/package.json: FORCE
	$(CMD_PREFIX)mkdir -p $(dir $(OUTPUTDIR))
	$(CMD_PREFIX)if [ ! -d $(OUTPUTDIR) ] ; then \
		$(TARGET_PRINTF) CLONE DuetWebControl "" $(CONFIG) "$(BUILD_ARCH)" ;\
		git clone -q --single-branch --branch master $(DuetWebControl-repo) $(OUTPUTDIR) ;\
	else \
		$(TARGET_PRINTF) PULL DuetWebControl "" $(CONFIG) "all" ;\
		git -C $(OUTPUTDIR) pull -q ;\
	fi

# We really don't need to wait on a build if package.json hasn't changed
$(CONFIGDIR)/all/publish/DuetWebControl/dist/DuetWebControl-SBC.zip: private MSG = "This could take a minute"
$(CONFIGDIR)/all/publish/DuetWebControl/dist/DuetWebControl-SBC.zip: $(CONFIGDIR)/all/publish/DuetWebControl/package.json
	$(CMD_PREFIX)$(TARGET_PRINTF) DEPS DuetWebControl "" $(CONFIG) "$(BUILD_ARCH)" $(MSG)
	$(CMD_PREFIX){ cd $(OUTPUTDIR) ; npm install >/dev/null 2>&1 || npm install ; }
	$(CMD_PREFIX)$(TARGET_PRINTF) BUILD DuetWebControl "" $(CONFIG) "$(BUILD_ARCH)" $(MSG)
	$(CMD_PREFIX){ cd $(OUTPUTDIR) ; npm run build >/dev/null 2>&1 || npm run build ; }

# Targets can have 3 things on the right side of the ":".
#   A variable assignment that will be specific to the target.
#   A list of prerequisites.
#   A simple rule to build the target
# If there are no rules for the target, make will apply any
# variable assignments and build any prerequisites then attempt
# to run a generic rule like "%-build" that matches.

# We need to override the architecture for DWC (and DuetSD) because
# they apply to all architectures and we're going to set a
# target-specific variable OUTPUTDIR which will be used for all
# DuetWebControl-build rules.  We're also going to depend on the
# zip file.  We don't want to run the generic "%-build" rule though
# so we give DuetWebControl-build a dummy rule that does nothing.
DuetWebControl.build: OUTPUTDIR = $(BINDIR)/publish/DuetWebControl
DuetWebControl.build: $(CONFIGDIR)/all/publish/DuetWebControl/dist/DuetWebControl-SBC.zip
DuetWebControl.build: ;

# All other -build targets can use this one generic rule.
# The $* always refers to the target "stem" so if the target
# DuetControlServer-build, the stem would be DuetControlServer.
%.build:
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) -C src/$* V=$(V) CONFIG=$(CONFIG) ARCH=$(BUILD_ARCH)

# The publish targets prepare the build products for the next steps.
# For the dotnet targets, "dotnet publish" is run.
#
# DuetRuntime and DWC don't really need a publish but we need
# them to have -publish targets for the next steps.
DuetRuntime.publish: $(DIRS_PUBLISH)
	$(ECHO_PREFIX)$(TARGET_TITLE)

DuetWebControl.publish: DuetWebControl.build
	$(ECHO_PREFIX)$(TARGET_TITLE)

# Each of the dotnet projects creates its own copy of the runtime
# so as each projects publishes, we're going to accumulate all of
# their runtimes in the DuetRuntime directory then delete the
# runtime from the project directory.
%.publish: OUTPUTDIR = $(BINDIR)/publish/$*
%.publish: %.build
	$(CMD_PREFIX)rm -rf $(OUTPUTDIR)
	$(CMD_PREFIX)mkdir -p $(OUTPUTDIR)
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) -C src/$* V=$(V) CONFIG=$(CONFIG) ARCH=$(BUILD_ARCH) DESTDIR=$(OUTPUTDIR) publish
	$(CMD_PREFIX)mkdir -p $(BINDIR)/publish/DuetRuntime/
	$(CMD_PREFIX)find $(OUTPUTDIR) -type f ! -name $(*)* ! -name web.config ! -name appsettings.* -exec mv '{}' $(BINDIR)/publish/DuetRuntime/ ';' ; \

# Buildroots are directories prepared for packaging.
#
# DuetTools is a composite of 4 dotnet projects.
# We need LCTARGET in case there are any static files in
# the pkg/common directory we need to copy.  Those directories
# are lower case.
DuetTools.buildroot: OUTPUTDIR = $(BINDIR)/buildroot/DuetTools
DuetTools.buildroot: LCTARGET = duettools
DuetTools.buildroot: CodeConsole.buildroot CodeLogger.buildroot CustomHttpEndpoint.buildroot ModelObserver.buildroot PluginManager.buildroot
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)rm -rf $(OUTPUTDIR)
	$(CMD_PREFIX)mkdir -p $(OUTPUTDIR)
	$(CMD_PREFIX)[ -d pkg/common/$(LCTARGET)/ ] && $(RSYNC) pkg/common/$(LCTARGET)/. $(OUTPUTDIR)/ || :
	$(CMD_PREFIX)for sourcedir in $(basename $^) ; do \
		$(RSYNC) $(BINDIR)/buildroot/$${sourcedir}/. $(OUTPUTDIR)/ ;\
	done

# There's not much to DuetSD, just a skeleton sd filesystem
# and a sample config.g in the pkg/common/duetsd directory.
DuetSD.buildroot: OUTPUTDIR = $(BINDIR)/buildroot/DuetSD
DuetSD.buildroot: LCTARGET = duetsd
DuetSD.buildroot:
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)rm -rf $(OUTPUTDIR)
	$(CMD_PREFIX)mkdir -p $(OUTPUTDIR)/opt/dsf/sd/{filaments,gcodes,macros,firmware,sys}
	$(CMD_PREFIX)[ -d pkg/common/$(LCTARGET)/ ] && $(RSYNC) pkg/common/$(LCTARGET)/. $(OUTPUTDIR)/ || :

# DuetSoftwareFramework actually has no files but we do check
# pkg/common/ anyway just in case.
DuetSoftwareFramework.buildroot: OUTPUTDIR = $(BINDIR)/buildroot/DuetSoftwareFramework
DuetSoftwareFramework.buildroot: LCTARGET = duetsoftwareframework
DuetSoftwareFramework.buildroot:
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)rm -rf $(OUTPUTDIR)
	$(CMD_PREFIX)mkdir -p $(OUTPUTDIR)
	$(CMD_PREFIX)[ -d pkg/common/$(LCTARGET)/ ] && $(RSYNC) pkg/common/$(LCTARGET)/. $(OUTPUTDIR)/ || :

# DWC does special work to create the buildroot.
DuetWebControl.buildroot: OUTPUTDIR = $(BINDIR)/buildroot/DuetWebControl
DuetWebControl.buildroot: LCTARGET = duetwebcontrol
DuetWebControl.buildroot: DuetWebControl.publish
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)rm -rf $(OUTPUTDIR)
	$(CMD_PREFIX)mkdir -p $(OUTPUTDIR)/opt/dsf/dwc2 $(OUTPUTDIR)/opt/dsf/sd
	$(CMD_PREFIX)[ -d pkg/common/$(LCTARGET)/ ] && $(RSYNC) pkg/common/$(LCTARGET)/. $(OUTPUTDIR)/ || :
	$(CMD_PREFIX)unzip -q $(CONFIGDIR)/all/publish/DuetWebControl/dist/DuetWebControl-SBC.zip -d $(OUTPUTDIR)/opt/dsf/dwc2/
	$(CMD_PREFIX){ cd $(OUTPUTDIR)/opt/dsf/sd ; ln -s ../dwc2 www ; }

# Everything else gets the generic rule.
%.buildroot: OUTPUTDIR = $(BINDIR)/buildroot/$*
%.buildroot: LCTARGET = $(shell echo $* | tr '[:upper:]' '[:lower:]')
%.buildroot: %.publish
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)rm -rf $(OUTPUTDIR)
	$(CMD_PREFIX)mkdir -p $(OUTPUTDIR)/opt/dsf/bin/
	$(CMD_PREFIX)$(RSYNC) $(BINDIR)/publish/$(*)/. $(OUTPUTDIR)/opt/dsf/bin/
	$(CMD_PREFIX)[ -d pkg/common/$(LCTARGET)/ ] && $(RSYNC) pkg/common/$(LCTARGET)/. $(OUTPUTDIR)/ || :

# Time to start the packages

# These variables are common to both package types
# Because of the "=", they don't get expanded until
# used by a specific target.
VERSION = $($(*)-version)
LCTARGET = $(shell echo $* | tr '[:upper:]' '[:lower:]')
BUILDROOT = $(BINDIR)/buildroot/$(*)/
PKGDIR = $(DESTDIR)/packages/$(PKG)
PKGSRCDIR = $(PKGDIR)/$(PKGNAME)

%.pkgcommon: %.buildroot
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)mkdir -p $(PKGSRCDIR)
	$(CMD_PREFIX)$(RSYNC) $(BUILDROOT)/. $(PKGSRCDIR)/
	$(CMD_PREFIX)[ -d pkg/$(PKG)/$(LCTARGET)/ ] && $(RSYNC) pkg/$(PKG)/$(LCTARGET)/. $(PKGSRCDIR)/ || :

DuetRuntime.rpm: private MSG = "This could take a minute. $(if $(KEY_ID),, No signing key)"
# We have to change "all" to "noarch" for rpm.
%.rpm: PKGARCH = $(BUILD_ARCH:all=noarch)
%.rpm: PKG = rpm
%.rpm: RELEASE = 950
%.rpm: PKGNAME = $(LCTARGET)-$(VERSION)-$(RELEASE).$(PKGARCH)
%.rpm: private MSG = $(if $(KEY_ID),," No signing key")
%.rpm: %.pkgcommon
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)rpmbuild --quiet --nodebuginfo --noclean --nocheck \
		--noprep --build-in-place --buildroot $(PKGSRCDIR) \
		--target=$(PKGARCH) \
		--define="%_rpmfilename $(PKGNAME).rpm" \
		--define="%_release $(RELEASE)" \
		--define="%_rpmdir $(PKGDIR)" \
		--define="%_arch $(PKGARCH)" \
		--define="%_tversion $(VERSION)" \
		--define="%_smp_build_ncpus 1" \
		--define="%_build_type $(CONFIG)" \
		-bb pkg/rpm/$(LCTARGET).spec >/dev/null
	$(CMD_PREFIX)rm -rf $(PKGSRCDIR)
ifneq ($(KEY_ID),)
	$(CMD_PREFIX)rpmsign --addsign --key-id=$(KEY_ID) $(PKGDIR)/$(PKGNAME).rpm >/dev/null
endif

DuetRuntime.deb: private MSG = "This could take a minute. $(if $(KEY_ID),, No signing key)"
# The DSF package references the DWC version so we have to package
# DWC first.
DuetSoftwareFramework.deb: DuetWebControl.deb
%.deb: PKGARCH = $(BUILD_ARCH)
%.deb: PKG = deb
%.deb: PKGNAME = $(LCTARGET)_$(VERSION)_$(PKGARCH)
%.deb: private MSG = $(if $(KEY_ID),," No signing key")
%.deb: %.pkgcommon
	$(ECHO_PREFIX)$(TARGET_TITLE)
	$(CMD_PREFIX)sed -i "s/TARGET_ARCH/$(PKGARCH)/g" $(PKGSRCDIR)/DEBIAN/{control,changelog}
	$(CMD_PREFIX)sed -i "s/DCSVER/$(DuetControlServer-version)/g" $(PKGSRCDIR)/DEBIAN/{control,changelog}
	$(CMD_PREFIX)sed -i "s/DWSVER/$(DuetWebServer-version)/g" $(PKGSRCDIR)/DEBIAN/{control,changelog}
	$(CMD_PREFIX)sed -i "s/SDVER/$(DuetSD-version)/g" $(PKGSRCDIR)/DEBIAN/{control,changelog}
	$(CMD_PREFIX)sed -i "s/DWCVER/$(DuetWebControl-version)/g" $(PKGSRCDIR)/DEBIAN/{control,changelog} 2>/dev/null || :
	$(CMD_PREFIX)dpkg-deb --build $(PKGSRCDIR) $(PKGDIR) >/dev/null
	$(CMD_PREFIX)rm -rf $(PKGSRCDIR)
ifneq ($(KEY_ID),)
	$(CMD_PREFIX)dpkg-sig -k $(KEY_ID) -s builder $(PKGDIR)/$(PKGNAME).deb  >/dev/null
endif

%.info:
	@echo "$* version:     $($(*)-version)"
	@printenv

info:
	@echo "DuetControlServer version:     $(DuetControlServer-version)"
	@echo "DuetWebServer version:         $(DuetWebServer-version)"
	@echo "DuetTools version:             $(DuetTools-version)"
	@echo "DuetSD version:                $(DuetSD-version)"
	@echo "DuetSoftwareFramework version: $(DuetSoftwareFramework-version)"
	@echo "DuetWebControl version:        $(DuetWebControl-version)"

%.clean:
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) -C src/$* clean V=$(V)

release-armhf-debs: BUILD_ARCH = armhf
release-armhf-debs: debs

release-arm64-debs: BUILD_ARCH = arm64
release-arm64-debs: debs

release-debs:
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=armhf debs
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=arm64 debs

release-armhfp-rpms: BUILD_ARCH = armhfp
release-armhfp-rpms: rpms

release-aarch64-rpms: BUILD_ARCH = aarch64
release-aarch64-rpms: rpms

release-rpms:
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=armhfp rpms
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=aarch64 rpms


release:
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=armhf debs
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=armhfp rpms
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=arm64 debs
	$(CMD_PREFIX)$(MAKE) $(MAKEOPTS) ARCH=aarch64 rpms
