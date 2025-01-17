# SourceGen Test Data #

This directory contains various regression tests.

NOTE: some tests may fail if you use a version of the assembler that is
different from the one used to generate the expected output.  The current
set was generated for:

 * 64tass v1.56.2625
 * ACME v0.97
 * cc65 v2.18
 * Merlin 32 v1.0


## Generator/Assembler Tests ##

Files with names like "10000-nifty-test" are regression test data files
for the code generator.  The test harness identifies them by filename
pattern: five digits, a hyphen, then one or more alphanumeric and
hyphens.  Files with a '.' or '_' are ignored.

If the leading number is between 10000 and 19999, inclusive, the test file
will be loaded as a new project.  A load address of $1000 is assumed.
The CPU type is determined by the last digit: 0 for 6502, 1 for 65C02,
2 for 65816, and 3 for W65C02.  Undocumented opcodes are enabled.  As with
all new projects, the first byte will be tagged as a code start point.  The
entry flags are currently set to emulation mode, but tests should not rely
on that.

If the leading number is 20000 or greater, the test file will be loaded as
a saved project.  A file with the same name plus a ".dis65" extension will
be opened as the project file.

### Overriding Settings ###

All tests are run with a fixed set of app settings, so that the tests look
the same regardless of how the assemblers are configured in the app settings
file.  For example, upper-case conversion is disabled, and cycle counts are
not shown.

Sometimes a test will want to exercise one of these settings, so we need
a way to tell the test harness to override the default.  We do this by
creating project symbols:

| Name                       | Value | Description
| -------------------------- | ----- | -----------------------------------------|
| __ENABLE_LABEL_NEWLINE     | any   | Puts long labels on their own line       |
| __ENABLE_ALL_LABEL_NEWLINE | any   | Puts all labels on their own line        |
| __ENABLE_CYCLE_COUNTS      | any   | Adds cycle count to end-of-line comments |

### Execution ###

With debug features enabled, you can open the test runner from the menu
with Debug > Source Generation Tests.  Click "Run Test" to run all tests.

For each test, the test harness will create a new project or open the
project file.  For every known assembler, the test harness will generate
source code, and compare it to the corresponding entry in the Expected
directory.  If they don't match exactly, a failure is reported for the
generation phase.  (These are text files, so the line terminators are not
required to match.)  Next, the generated sources are fed to the appropriate
cross-assembler, whether or not the sources matched expectations.  If the
assembler reports success, the output file is compared to the original data
file.  If these match, success is reported for the assembly phase.

The top window in the test harness shows a summary of success or failure.
The bottom window shows details reported for each individual test.  Use
the drop list to select which test is shown.

The generated sources and assembled output is placed into a temporary
directory inside SGTestData that is named after the test.  For example,
test 10000-allops-value-6502 will have all of its generated output in a
directory called "tmp10000".  If all parts of the test are successful, the
directory will be removed.  If generation or assembly fails, or if you check
the "retain output" box in the test harness, the directory and its contents
will remain.  This allows you to examine the outputs when investigating
failures.

As a safety measure, a directory will NOT be removed if it contains files
that the test harness doesn't recognize.

### Updating Tests ###

If you want to add or update a test, follow these steps:

 1. Make the changes to the test data file and test project file.
 2. Run the test harness.  The generation test will fail and leave output in
    the tmpNNNNN directory.  Make sure the assembly test is succeeding.
 3. After verifying that the generated sources look correct, copy them
    into the Expected directory, replacing any existing copies.
 4. Run the test harness.  This should now report success, and will
    remove the tmpNNNNN directory.
 5. Run "git diff" to verify that the changes match your expectations.

Be sure to have the version of the cross-assembler identified at the top
of this document configured.


### Other Notes ###

The original source code used to generate the test cases can be found
in the Source directory.  The test harness does not use these files.  If
you want to update a test file, you will need to run the assembler
yourself.  The assembler used is noted in a comment at the top of the file.

The code is not required to do anything useful.  Many of the test cases
would crash or hang if executed.


## FunkyProjects ##

This is a collection of project files with deliberate errors.  These exist
to exercise the load-time error reporting.  See the README in that directory
for a full explanation.


## Visualization ##

Some test projects and data files for exercising the visualization generators.
Not part of a formal test; load the projects and eyeball the results.
