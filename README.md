# zvt
zvt is a tiny command line utility to control electronic payment terminals using the ZVT interface.

## Usage
### Send payment request
```
zvt.exe -pay 15.32
```
Sends a payment request for 15.32 EUR to the terminal. Exit code 0 means success.

### Create daily statement
```
zvt.exe -endofday
```
Creates a daily statement. Exit code 0 means success.

## Known issues
zvt is only tested with Verifone H5000 terminals.
