#ifndef SELFREF_H
#define SELFREF_H
int spell(int x);
#define spell(x) spell((x) + 1)
#endif
