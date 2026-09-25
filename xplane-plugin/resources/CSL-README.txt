SkyPilot - CSL models
=====================

The SkyPilot X-Plane plugin draws the other SkyNetwork aircraft with CSL models.
No models are included with the plugin, so you need to install at least one
CSL package into this folder before other aircraft can be shown.

1. Download a CSL package, for example the free "Bluebell" CSL package.
2. Extract it so that each package gets its own sub-folder here, e.g.

     X-Plane/Resources/plugins/SkyPilot/Resources/CSL/Bluebell/...

   (Each package folder contains one or more "xsb_aircraft.txt" files.)
3. Restart X-Plane (or reload plugins).

X-Plane's Log.txt shows how many models were loaded (look for lines starting
with "SkyPilot:"). If no models are found, the SkyPilot app shows a warning.
