# SkyPilot X-Plane plugin (X-Plane 11/12)
#
# Build (Linux / macOS, single-config generators):
#   cmake -S xplane-plugin -B build/xp
#   cmake --build build/xp -j
# Build (Windows, Visual Studio):
#   cmake -S xplane-plugin -B build/xp -A x64
#   cmake --build build/xp --config Release
# Package (creates <prefix>/SkyPilot/ ready to copy into X-Plane/Resources/plugins/):
#   cmake --install build/xp --config Release --prefix build/xp/dist
#
# XPMP2 (https://github.com/TwinFan/XPMP2, MIT license) is downloaded at configure
# time. It renders the other aircraft and also ships the X-Plane SDK headers/libraries
# and the resource files the plugin needs at runtime.

cmake_minimum_required(VERSION 3.16)

# Only matters on macOS (XPMP2 would otherwise default to a universal build).
if(APPLE AND NOT CMAKE_OSX_ARCHITECTURES)
    set(CMAKE_OSX_ARCHITECTURES "x86_64;arm64" CACHE STRING "Archs to build")
endif()

project(SkyPilotXPlanePlugin
        VERSION 1.0.0
        DESCRIPTION "SkyPilot plugin for X-Plane 11/12"
        LANGUAGES C CXX)

# Default to Release for single-config generators. This must be set before XPMP2 is
# added, because XPMP2 selects its optimization/runtime flags from CMAKE_BUILD_TYPE.
get_property(_isMultiConfig GLOBAL PROPERTY GENERATOR_IS_MULTI_CONFIG)
if(NOT _isMultiConfig AND NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

################################################################################
# XPMP2
################################################################################

set(SKYPILOT_XPMP2_TAG "v3.7.0" CACHE STRING "XPMP2 release tag to build against")

include(FetchContent)
FetchContent_Declare(xpmp2
    GIT_REPOSITORY https://github.com/TwinFan/XPMP2.git
    GIT_TAG        ${SKYPILOT_XPMP2_TAG}
    GIT_SHALLOW    TRUE
)
FetchContent_MakeAvailable(xpmp2)   # adds target XPMP2 (static library)

set(XPMP2_DIR "${xpmp2_SOURCE_DIR}")
set(XPLANE_SDK_DIR "${XPMP2_DIR}/lib/SDK")

################################################################################
# Plugin target
################################################################################

add_library(SkyPilot MODULE
    src/SkyPilotPlugin.cpp
    src/UdpSocket.cpp
    src/UdpSocket.h
)

target_include_directories(SkyPilot PRIVATE
    ${XPLANE_SDK_DIR}/CHeaders/XPLM
    ${XPLANE_SDK_DIR}/CHeaders/Widgets
    ${XPMP2_DIR}/inc
)

# X-Plane SDK API levels (as required by XPMP2) and platform macros.
target_compile_definitions(SkyPilot PRIVATE
    XPLM200=1 XPLM210=1 XPLM300=1 XPLM301=1 XPLM303=1 XPLM400=1
    APL=$<BOOL:${APPLE}>
    IBM=$<BOOL:${WIN32}>
    LIN=$<AND:$<BOOL:${UNIX}>,$<NOT:$<BOOL:${APPLE}>>>
)

if(MSVC)
    target_compile_definitions(SkyPilot PRIVATE
        _WIN32_WINNT=0x0601 WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
    target_compile_options(SkyPilot PRIVATE /W4 /EHsc /utf-8 /Zc:__cplusplus /wd4996 /wd4068)
    # XPMP2 is always compiled against the release DLL runtime (/MD); match it in every configuration.
    set_property(TARGET SkyPilot PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreadedDLL")
elseif(WIN32)
    target_compile_definitions(SkyPilot PRIVATE _WIN32_WINNT=0x0601 WIN32_LEAN_AND_MEAN NOMINMAX)
    target_compile_options(SkyPilot PRIVATE -Wall -Wextra -fexceptions)
else()
    target_compile_options(SkyPilot PRIVATE
        -Wall -Wextra -Wshadow -Wno-unused-parameter -Wno-deprecated-declarations
        -fexceptions -fvisibility=hidden)
endif()

if(APPLE)
    set(CMAKE_OSX_DEPLOYMENT_TARGET 10.15)
    target_compile_options(SkyPilot PRIVATE -mmacosx-version-min=10.15)
    target_link_options(SkyPilot PRIVATE -mmacosx-version-min=10.15)
endif()

# Link XPMP2 and the platform's X-Plane plugin library.
target_link_libraries(SkyPilot PRIVATE XPMP2)

if(WIN32)
    find_library(XPLM_LIBRARY NAMES XPLM_64 XPLM_64.lib
                 PATHS "${XPLANE_SDK_DIR}/Libraries/Win" NO_DEFAULT_PATH REQUIRED)
    # Winsock (plugin + XPMP2 networking) and iphlpapi (XPMP2 uses GetAdaptersAddresses).
    target_link_libraries(SkyPilot PRIVATE ${XPLM_LIBRARY} ws2_32 wsock32 iphlpapi)
    if(MINGW)
        target_link_options(SkyPilot PRIVATE -static-libgcc -static-libstdc++)
    endif()
elseif(APPLE)
    find_library(XPLM_LIBRARY NAMES XPLM PATHS "${XPLANE_SDK_DIR}/Libraries/Mac" NO_DEFAULT_PATH REQUIRED)
    find_library(IOKIT_LIBRARY IOKit REQUIRED)
    find_library(CORE_FOUNDATION_LIBRARY CoreFoundation REQUIRED)
    target_link_libraries(SkyPilot PRIVATE ${XPLM_LIBRARY} ${IOKIT_LIBRARY} ${CORE_FOUNDATION_LIBRARY})
    # XPLM symbols are resolved when X-Plane loads the plugin.
    target_link_options(SkyPilot PRIVATE -undefined dynamic_lookup)
else()
    # Linux: there is no XPLM stub library; XPLM symbols are resolved when X-Plane loads the plugin.
    set(THREADS_PREFER_PTHREAD_FLAG TRUE)
    find_package(Threads REQUIRED)
    target_link_libraries(SkyPilot PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
    # Export only the X-Plane entry points.
    target_link_options(SkyPilot PRIVATE
        -Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/src/SkyPilot.sym
        $<$<CONFIG:Release>:-s>)
endif()

# Output: <build>/<platform>_x64/SkyPilot.xpl
if(WIN32)
    set(SKYPILOT_PLATFORM_DIR "win_x64")
elseif(APPLE)
    set(SKYPILOT_PLATFORM_DIR "mac_x64")
else()
    set(SKYPILOT_PLATFORM_DIR "lin_x64")
endif()

set_target_properties(SkyPilot PROPERTIES
    PREFIX ""
    OUTPUT_NAME "SkyPilot"
    SUFFIX ".xpl"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/${SKYPILOT_PLATFORM_DIR}"
    # Multi-config generators (Visual Studio) would otherwise add a Release/ Debug/ sub-folder.
    LIBRARY_OUTPUT_DIRECTORY_DEBUG "${CMAKE_BINARY_DIR}/${SKYPILOT_PLATFORM_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/${SKYPILOT_PLATFORM_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_RELWITHDEBINFO "${CMAKE_BINARY_DIR}/${SKYPILOT_PLATFORM_DIR}"
    LIBRARY_OUTPUT_DIRECTORY_MINSIZEREL "${CMAKE_BINARY_DIR}/${SKYPILOT_PLATFORM_DIR}"
)

################################################################################
# Package: <prefix>/SkyPilot/
#   <platform>_x64/SkyPilot.xpl
#   Resources/  (XPMP2 resource files)
#   Resources/CSL/README.txt  (users put their CSL packages here)
################################################################################

install(TARGETS SkyPilot
        LIBRARY DESTINATION "SkyPilot/${SKYPILOT_PLATFORM_DIR}"
        RUNTIME DESTINATION "SkyPilot/${SKYPILOT_PLATFORM_DIR}")

install(FILES
        "${XPMP2_DIR}/Resources/Doc8643.txt"
        "${XPMP2_DIR}/Resources/related.txt"
        "${XPMP2_DIR}/Resources/relOp.txt"
        "${XPMP2_DIR}/Resources/Obj8DataRefs.txt"
        "${XPMP2_DIR}/Resources/MapIcons.png"
        DESTINATION "SkyPilot/Resources")

install(DIRECTORY "${XPMP2_DIR}/Resources/Contrail"
        DESTINATION "SkyPilot/Resources")

install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/resources/CSL-README.txt"
        DESTINATION "SkyPilot/Resources/CSL"
        RENAME "README.txt")

install(FILES "${XPMP2_DIR}/LICENSE"
        DESTINATION "SkyPilot"
        RENAME "XPMP2-LICENSE.txt")
