/* C-linkage bridge to the C++ sensor hub. Shared by main.c (compiled as C) and hub.cpp (C++). */
#ifndef HUB_H
#define HUB_H

#ifdef __cplusplus
extern "C" {
#endif

void hub_init(void);
int  hub_sample(void);

#ifdef __cplusplus
}
#endif

#endif /* HUB_H */
