# LaserCAM Add-ins - Privacy Notice

Version 1.1, 16 November 2026

This notice explains what the LaserCAM add-ins for SolidWorks and Autodesk Fusion and the LaserCAM Odoo Connector store and send. The controller is MB UCASE, company code 307593691, Tremtinių g. 5, Teleičių k., Kauno r. sav., LT-53273, Lithuania, info@ucase.eu.

The website ucase.eu and the LaserCAM web application are covered by the privacy and cookie policy at https://www.ucase.eu/privacy.

## What stays on your computer

- Settings, in your user profile (on Windows `%APPDATA%\LaserCAM\`): the LaserCAM contact id and key, your Odoo address and Odoo API key, and defaults such as gas, sheet size and part spacing.
- On Windows the keys are stored encrypted with Windows data protection for your Windows user (SolidWorks add-in, Fusion add-in and the Connector). On macOS the Fusion add-in stores the keys in the settings file without encryption.
- A diagnostic log file in the same folder. It is not sent anywhere. Keys are never written to the log in full.

## What is sent to the LaserCAM server (laser.ucase.eu)

- Only when you click a LaserCAM command: the flat pattern of the part (DXF), its number, material, thickness, quantity and the nesting settings, together with your LaserCAM contact id and key so that the server can check your plan. The Connector sends the files attached to the Odoo products you ticked (DXF, STEP, PDF drawing).
- If you use "Save to Odoo" from LaserCAM: your Odoo address and Odoo API key travel with the job. They are held only in the memory of the LaserCAM server while the job exists (up to 1 hour), are not written to disk and are never sent to the browser.
- The server uses the geometry only to produce the nesting, weight, cutting time and price. A job is held in the server's memory until it expires (up to 1 hour) and is not written to disk. A STEP file is written to a temporary file while it is being calculated and deleted right after. The server's technical logs keep the time of the request, its address and the IP address and, for STEP files, the file name and size.

## Checking for a newer version

- From version 1.4.0, when you use a command (or start the Connector), the add-in may ask the LaserCAM server whether a newer version exists. It does this at most once a day.
- The request contains no data about you, your computer or your files. Like any request, it reaches the server with your IP address.
- If a newer version exists, you are asked whether to open the download page. Nothing is downloaded or installed without your click. The date of the last check and your answer are kept in `update-check.json` in the settings folder. You can switch the check off in the settings (`CHECK_UPDATES`).

## What is sent to your own Odoo

- Directly from the add-in or from the LaserCAM server, with your API key: product lookup by number, weight, the flat pattern DXF and, if enabled, the CAD file, a STEP export and the drawing as attachments, and the bill of materials and operation time. This data goes to the Odoo address you entered and to nobody else.
- If you choose "Send to LaserCAM only", or leave the Odoo settings empty, nothing is read from or written to Odoo.

## What the add-ins do not do

- They do not read or send files other than the part or assembly you run the command on, and the files of the Odoo products you ticked.
- They do not collect usage statistics and contain no advertising or third-party trackers. The only addresses they contact are the LaserCAM server and the Odoo address you entered. The download of a newer version opens in your browser from GitHub.
- They do not work in the background: nothing is sent until you click a command or start the Connector.

When a command opens LaserCAM in your browser, the web application's own privacy and cookie policy applies there.

## Why we process this data

To provide the service you asked for (performance of a contract) and to keep the service secure. The data is mostly technical drawings, not personal data; the personal data involved is your account identifier and, in your own Odoo, whatever your Odoo holds.

## Your rights

You can ask us what we hold about your account, and ask us to correct or delete it: info@ucase.eu. You can remove everything stored locally by uninstalling the add-in and deleting the folder `%APPDATA%\LaserCAM`. You may also complain to the State Data Protection Inspectorate of Lithuania (vdai.lrv.lt).
