static int cleaned;
void banish_cleanup(int *p) { cleaned += *p; }
int cleaned_value(void) { return cleaned; }
