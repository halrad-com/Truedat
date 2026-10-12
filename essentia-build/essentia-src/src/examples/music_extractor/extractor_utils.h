#include <essentia/pool.h>
#include <essentia/algorithmfactory.h> 

using namespace std;
using namespace essentia;

void setExtractorDefaultOptions(Pool &options);
void setExtractorOptions(const std::string& filename, Pool& options);
void mergeValues(Pool& pool, Pool& options);
void outputToFile(Pool& pool, const string& outputFilename, Pool& options);
// Writes the pool as an ESPB binary file (BinaryPoolOutput). The optional
// profile key 'framesInclude' (a list of descriptor name prefixes) becomes the
// writer's 'include' filter; absent or empty = every descriptor.
void outputToBinaryFile(Pool& pool, const string& outputFilename, Pool& options, const string& tag);

