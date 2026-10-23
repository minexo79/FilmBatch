// FilmBatch ProPhoto output profile generator (build-time tool only, never shipped).
//
// Generates src/FilmBatch.Engine/Imaging/Raw/Profiles/prophoto-gamma18.icc:
// an ICC v4.3 display profile with FilmBatch ProPhoto RGB primaries
// (ColorSpaces.ProPhoto in src/FilmBatch.Raw/Color/ColorMath.cs),
// D50 white, and a pure parametric gamma 1.8 TRC on all three channels
// (no ROMM toe, no flare). Build with:
//
//   cc -O2 -I/opt/homebrew/include -L/opt/homebrew/lib \
//     -o /tmp/prophoto-gen tools/prophoto-icc/gen.c -llcms2
//   /tmp/prophoto-gen src/FilmBatch.Engine/Imaging/Raw/Profiles/prophoto-gamma18.icc
//
// Pinned toolchain: Little CMS 2.17 (MIT), Homebrew little-cms2 2.17.
// The output is byte-deterministic: fixed description/copyright strings,
// the creation-date field is pinned after canonicalization, and the MD5
// profile ID is computed from those pinned bytes. The expected output hash
// is recorded in README.md beside the regeneration command.
//
// The ICC bytes are FilmBatch's own numeric rendering of published
// colorimetry (primaries + D50 + gamma 1.8), not a copy of any vendor
// profile: no downloaded profile was used as input. License care:
// LCMS itself is MIT (Marti Maria Saguer); the CMS library is not linked
// into the shipped app by this tool.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <lcms2.h>

int main(int argc, char** argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: gen OUTPUT.icc\n");
        return 2;
    }
    if (LCMS_VERSION != 2170 || cmsGetEncodedCMMversion() != 2170) {
        fprintf(stderr, "Requires Little CMS 2.17 headers and runtime\n");
        return 1;
    }

    // FilmBatch ProPhoto primaries = ColorSpaces.ProPhoto (ROMM primaries).
    const cmsCIExyY white = {0.3457, 0.3585, 1.0};
    const cmsCIExyYTRIPLE primaries = {
        {0.734699, 0.265301, 1.0},
        {0.159597, 0.840403, 1.0},
        {0.036598, 0.000105, 1.0},
    };
    cmsToneCurve* gamma18[3] = {
        cmsBuildGamma(NULL, 1.8), cmsBuildGamma(NULL, 1.8), cmsBuildGamma(NULL, 1.8)};
    if (!gamma18[0] || !gamma18[1] || !gamma18[2]) {
        fprintf(stderr, "cmsBuildGamma failed\n");
        return 1;
    }
    cmsHPROFILE profile = cmsCreateRGBProfile(&white, &primaries, gamma18);
    cmsFreeToneCurveTriple(gamma18);
    if (!profile) {
        fprintf(stderr, "cmsCreateRGBProfile failed\n");
        return 1;
    }

    // v4 retains gamma as s15Fixed16; v2 curv rounds 1.8 down to 1.796875.
    cmsSetProfileVersion(profile, 4.3);
    cmsSetHeaderManufacturer(profile, (cmsUInt32Number)0x46424D42 /* 'FBMB' */);
    cmsSetHeaderModel(profile, (cmsUInt32Number)0x50503138 /* 'PP18' */);

    cmsMLU* desc = cmsMLUalloc(NULL, 1);
    cmsMLU* copy = cmsMLUalloc(NULL, 1);
    if (!desc || !copy) {
        fprintf(stderr, "cmsMLUalloc failed\n");
        return 1;
    }
    cmsMLUsetASCII(desc, "en", "US", "FilmBatch ProPhoto RGB gamma 1.8");
    cmsMLUsetASCII(copy, "en", "US", "Copyright (c) 2026 FilmBatch. LCMS 2.17 (MIT); numeric ProPhoto primaries, D50, pure gamma 1.8.");
    if (!cmsWriteTag(profile, cmsSigProfileDescriptionTag, desc) ||
        !cmsWriteTag(profile, cmsSigCopyrightTag, copy)) {
        fprintf(stderr, "cmsWriteTag failed\n");
        return 1;
    }
    cmsMLUfree(desc);
    cmsMLUfree(copy);

    // Serialize once to canonicalize tag tables, then recompute the MD5
    // profile ID from the pinned bytes: LCMS would otherwise stamp the
    // wall clock into the creation-date field on every save.
    cmsUInt32Number size = 0;
    if (!cmsSaveProfileToMem(profile, NULL, &size) || size == 0) {
        fprintf(stderr, "cmsSaveProfileToMem (size) failed\n");
        return 1;
    }
    void* bytes = malloc(size);
    if (!bytes) {
        fprintf(stderr, "out of memory\n");
        return 1;
    }
    if (!cmsSaveProfileToMem(profile, bytes, &size)) {
        fprintf(stderr, "cmsSaveProfileToMem failed\n");
        return 1;
    }
    cmsCloseProfile(profile);

    // Pin the creation-date field (bytes 24..35) to 2026-01-01 00:00:00 UTC.
    memset((char*)bytes + 24, 0, 12);
    ((char*)bytes)[24] = 0x07;
    ((char*)bytes)[25] = (char)0xEA; // 2026
    ((char*)bytes)[27] = 1; // January
    ((char*)bytes)[29] = 1;

    cmsHPROFILE pinned = cmsOpenProfileFromMem(bytes, size);
    if (!pinned) {
        fprintf(stderr, "cmsOpenProfileFromMem failed\n");
        return 1;
    }
    if (!cmsMD5computeID(pinned)) {
        fprintf(stderr, "cmsMD5computeID failed\n");
        return 1;
    }
    cmsUInt32Number finalSize = 0;
    if (!cmsSaveProfileToMem(pinned, NULL, &finalSize) || finalSize == 0) {
        fprintf(stderr, "cmsSaveProfileToMem (final size) failed\n");
        return 1;
    }
    void* finalBytes = malloc(finalSize);
    if (!finalBytes) {
        fprintf(stderr, "out of memory\n");
        return 1;
    }
    if (!cmsSaveProfileToMem(pinned, finalBytes, &finalSize)) {
        fprintf(stderr, "cmsSaveProfileToMem (final) failed\n");
        return 1;
    }
    cmsCloseProfile(pinned);
    free(bytes);

    FILE* out = fopen(argv[1], "wb");
    if (!out) {
        perror("fopen");
        return 1;
    }
    if (fwrite(finalBytes, 1, finalSize, out) != finalSize) {
        perror("fwrite");
        return 1;
    }
    fclose(out);
    free(finalBytes);
    return 0;
}
