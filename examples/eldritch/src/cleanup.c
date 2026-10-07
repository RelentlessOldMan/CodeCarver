/* banish_cleanup is referenced ONLY inside an attribute, and lives in another file. */
void banish_cleanup(int *p);
int cleaned_value(void);
int cleanup_demo(void)
{
    {
        int __attribute__((cleanup(banish_cleanup))) v = 6;
        (void)v;
    }
    return cleaned_value();
}
