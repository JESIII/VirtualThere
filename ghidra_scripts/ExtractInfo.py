# Extract strings, function names, and imports from the analyzed binary
#@category VirtualThere
#@keyBinding
#@nrrpCategory
#@description Extract strings and function info

from ghidra.app.script import GhidraScript
from ghidra.program.model.listing import Function
from ghidra.program.model.symbol import SymbolUtilities
import re

# Extract strings (4+ chars)
print "=== STRINGS ==="
strings = currentProgram.getListing().getDefinedData(True)
for s in strings:
    val = s.getValue()
    if val and len(str(val)) >= 4:
        print "%s: %s" % (s.getAddress(), str(val))

# Extract function names
print "\n=== FUNCTIONS ==="
fm = currentProgram.getFunctionManager()
for func in fm.getFunctions(True):
    print str(func.getEntryPoint()) + ": " + func.getName()

# Extract imports
print "\n=== IMPORTS ==="
importManager = currentProgram.getImportManager()
for ext in currentProgram.getExternalManager().getExternals():
    print ext.getName()
